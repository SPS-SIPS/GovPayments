using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Domain.Callbacks;
using Sps.P2G.Gateway.Domain.Payments;
using Sps.P2G.Gateway.Domain.Security;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.RateLimiting;
using Sps.P2G.Gateway.Infrastructure.Security;
using Sps.P2G.Gateway.Infrastructure.SipsConnect;
using Sps.P2G.Gateway.Infrastructure.Time;
using Sps.P2G.Gateway.Modules.Shared;

namespace Sps.P2G.Gateway.Modules.Verification;

public static class SipsConnectEndpoints
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PaymentConfirmLocks = new(StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapSipsConnectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sips-connect")
            .WithTags("SIPS Connect")
            .RequireRateLimiting(RateLimitPolicies.SipsConnect);
        var authOptions = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        var keycloakOptions = app.ServiceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        if (keycloakOptions.Enabled && !authOptions.AllowSipsConnectInternalKeyFallback)
        {
            group.RequireAuthorization(AuthPolicies.SipsConnectClient);
        }

        group.MapPost("/verify", async (
            VerificationRequest request,
            HttpContext httpContext,
            GatewayDbContext db,
            IDescriptorService descriptorService,
            IHashChainService hashChain,
            IOptions<P2GOptions> options,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointAuth.HasInternalAccess(httpContext, options))
            {
                return Results.Unauthorized();
            }

            var contractError = ValidateVerificationContract(request);
            if (contractError is not null)
            {
                return Results.Ok(VerificationResponse.Rejected(contractError));
            }

            var verificationId = FirstNonEmpty(request.VerificationRequestId, request.VerificationId) ?? "";
            var invoiceLookup = FirstNonEmpty(request.InvoiceIdOrUpr);
            if (invoiceLookup is not null)
            {
                return await VerifyInvoiceAsync(request, invoiceLookup, verificationId, db, descriptorService, hashChain, clock, cancellationToken);
            }

            var accountNo = FirstNonEmpty(request.AccountNo);
            if (accountNo is null)
            {
                return Results.Ok(VerificationResponse.Rejected("NOT_FOUND"));
            }

            var accountType = FirstNonEmpty(request.Type, request.AccountType);
            if (accountType is not null && !IsBillLookupType(accountType))
            {
                var revenueAccount = await FindRevenueAccountAsync(db, accountNo, accountType, cancellationToken);
                if (revenueAccount is null)
                {
                    return Results.Ok(VerificationResponse.Rejected("ACCOUNT_NOT_FOUND"));
                }

                return Results.Ok(new VerificationResponse(
                    true,
                    "Account verified",
                    revenueAccount.AccountNumber,
                    revenueAccount.ClearingType,
                    revenueAccount.AccountName,
                    FirstNonEmpty(revenueAccount.MdaName, revenueAccount.ServiceName) ?? "",
                    revenueAccount.Currency));
            }

            return await VerifyInvoiceAsync(request, accountNo, verificationId, db, descriptorService, hashChain, clock, cancellationToken);
        });

        group.MapPost("/payments/confirm", async (
            PaymentRequest request,
            HttpContext httpContext,
            GatewayDbContext db,
            IHashChainService hashChain,
            IOptions<P2GOptions> options,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointAuth.HasInternalAccess(httpContext, options))
            {
                return Results.Unauthorized();
            }

            var txId = FirstNonEmpty(request.SipsTransactionId, request.TxId);
            SemaphoreSlim? paymentConfirmLock = null;
            if (txId is not null)
            {
                paymentConfirmLock = PaymentConfirmLocks.GetOrAdd(txId, _ => new SemaphoreSlim(1, 1));
                await paymentConfirmLock.WaitAsync(cancellationToken);
            }

            try
            {
            if (LooksLikeCompletionNotification(request))
            {
                return await HandlePaymentCompletionAsync(request, txId, db, hashChain, clock, cancellationToken);
            }

            var contractError = ValidatePaymentContract(request, txId);
            if (contractError is not null)
            {
                return Results.Ok(PaymentResponse.Rejected(request.EndToEndId, txId ?? "", contractError));
            }

            var paymentTxId = txId!;
            var paymentEndToEndId = request.EndToEndId!;
            var paymentCurrency = request.Currency!;
            var payloadJson = JsonDefaults.Serialize(request);
            var payloadHash = JsonDefaults.Sha256(payloadJson);
            var idempotencyKey = $"payment:confirm:{paymentTxId}";
            var idempotencyRecord = await db.IdempotencyRecords.FirstOrDefaultAsync(x => x.Key == idempotencyKey, cancellationToken);
            if (idempotencyRecord is not null && idempotencyRecord.PayloadHash != payloadHash)
            {
                return Results.Ok(PaymentResponse.Rejected(paymentEndToEndId, paymentTxId, "DUPLICATE_TX_DIFFERENT_PAYLOAD"));
            }

            if (idempotencyRecord is not null)
            {
                var existingAttempt = await db.BillPaymentAttempts.FirstOrDefaultAsync(
                    x => x.TxId == paymentTxId && x.Status == "ACSC" && x.Reason == "PAYMENT_ACCEPTED_PENDING_COMPLETION",
                    cancellationToken);
                if (existingAttempt is not null)
                {
                    return Results.Ok(PaymentResponse.Accepted(existingAttempt.EndToEndId, paymentTxId, "Duplicate payment accepted; awaiting completion notification"));
                }
            }

            var existingPayment = await db.BillPayments.FirstOrDefaultAsync(x => x.SipsTransactionId == paymentTxId || x.TxId == paymentTxId, cancellationToken);
            if (existingPayment is not null)
            {
                if (idempotencyRecord is null)
                {
                    db.IdempotencyRecords.Add(new IdempotencyRecord
                    {
                        Id = Guid.NewGuid(),
                        Key = idempotencyKey,
                        PayloadHash = payloadHash,
                        ResourceId = existingPayment.Id,
                        CreatedAt = clock.UtcNow
                    });
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                    {
                        db.ChangeTracker.Clear();
                    }
                }

                return Results.Ok(PaymentResponse.Accepted(existingPayment.EndToEndId, paymentTxId, "Duplicate payment already processed"));
            }

            var lookup = FirstNonEmpty(request.Upr, request.InvoiceId, request.BillReference, request.EndToEndId, ParseBillReference(request.RemittanceInformation));
            var bill = lookup is null ? null : await FindBillAsync(db, lookup, cancellationToken);
            if (bill is null)
            {
                await StoreAttemptAsync(db, request, null, null, "RJCT", "BILL_NOT_FOUND", clock, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(PaymentResponse.Rejected(paymentEndToEndId, paymentTxId, "BILL_NOT_FOUND"));
            }

            var account = await db.RevenueAccounts.FirstAsync(x => x.Id == bill.RevenueAccountId, cancellationToken);
            var validationError = ValidatePayment(request, bill, account, clock.UtcNow);
            if (validationError is not null)
            {
                if (validationError == "PAYMENT_IN_PROGRESS")
                {
                    var pendingDuplicateResponse = await TryBuildPendingPaymentResponseAsync(db, paymentTxId, payloadHash, paymentEndToEndId, cancellationToken);
                    if (pendingDuplicateResponse is not null)
                    {
                        return Results.Ok(pendingDuplicateResponse);
                    }
                }

                var duplicateResponse = await TryBuildDuplicatePaymentResponseAsync(db, paymentTxId, payloadHash, paymentEndToEndId, cancellationToken);
                if (duplicateResponse is not null)
                {
                    return Results.Ok(duplicateResponse);
                }

                await StoreAttemptAsync(db, request, bill.Id, bill.AuthorityId, "RJCT", validationError, clock, cancellationToken);
                await hashChain.AppendAsync(db, bill, BillEventTypes.PaymentRejected, new { bill.Id, bill.UPR, txId = paymentTxId, reason = validationError }, "SipsConnect", request.FromBIC ?? "", "SipsConnect", paymentTxId, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(PaymentResponse.Rejected(paymentEndToEndId, paymentTxId, validationError));
            }

            var now = clock.UtcNow;
            db.BillPaymentAttempts.Add(new BillPaymentAttempt
            {
                Id = Guid.NewGuid(),
                BillId = bill.Id,
                AuthorityId = bill.AuthorityId,
                TxId = paymentTxId,
                EndToEndId = paymentEndToEndId,
                Status = "ACSC",
                Reason = "PAYMENT_ACCEPTED_PENDING_COMPLETION",
                RawPayloadJson = payloadJson,
                CreatedAt = now
            });
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                Key = idempotencyKey,
                PayloadHash = payloadHash,
                ResourceId = bill.Id,
                CreatedAt = now
            });

            bill.StatusProjection = BillStatuses.PaymentInitiated;
            bill.UpdatedAt = now;

            var paidAmount = GetPaymentAmount(request);
            await hashChain.AppendAsync(db, bill, BillEventTypes.PaymentAccepted, new { bill.Id, bill.UPR, txId = paymentTxId, amount = paidAmount, currency = paymentCurrency, status = "PendingCompletion" }, "SipsConnect", request.FromBIC ?? "", "SipsConnect", paymentTxId, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                db.ChangeTracker.Clear();
                var duplicatePayment = await db.BillPayments.FirstOrDefaultAsync(
                    x => x.SipsTransactionId == paymentTxId || x.TxId == paymentTxId,
                    cancellationToken);
                if (duplicatePayment is null)
                {
                    var duplicateAttempt = await db.BillPaymentAttempts.FirstOrDefaultAsync(
                        x => x.TxId == paymentTxId && x.Status == "ACSC" && x.Reason == "PAYMENT_ACCEPTED_PENDING_COMPLETION",
                        cancellationToken);
                    if (duplicateAttempt is null)
                    {
                        throw;
                    }

                    return Results.Ok(PaymentResponse.Accepted(duplicateAttempt.EndToEndId, paymentTxId, "Duplicate payment accepted; awaiting completion notification"));
                }

                return Results.Ok(BuildDuplicatePaymentResponse(duplicatePayment, paymentTxId, payloadHash, paymentEndToEndId));
            }

            return Results.Ok(PaymentResponse.Accepted(paymentEndToEndId, paymentTxId, "Invoice payment accepted"));
            }
            finally
            {
                paymentConfirmLock?.Release();
            }
        });

        group.MapPost("/payments/reverse", async (
            ReturnRequest request,
            HttpContext httpContext,
            GatewayDbContext db,
            IHashChainService hashChain,
            IOptions<P2GOptions> options,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointAuth.HasInternalAccess(httpContext, options))
            {
                return Results.Unauthorized();
            }

            var originalTxId = FirstNonEmpty(request.OriginalSipsTransactionId, request.TxId);
            var reversalTxId = FirstNonEmpty(request.ReversalTransactionId, request.ReturnId);
            var contractError = ValidateReturnContract(request, originalTxId, reversalTxId);
            if (contractError is not null)
            {
                return Results.Ok(ReturnResponse.Rejected(originalTxId ?? "", request.EndToEndId, contractError));
            }

            var requiredOriginalTxId = originalTxId!;
            var requiredReversalTxId = reversalTxId!;
            var requiredEndToEndId = request.EndToEndId!;
            var returnReason = request.Reason!;
            var payloadJson = JsonDefaults.Serialize(request);
            var payloadHash = JsonDefaults.Sha256(payloadJson);
            var idempotencyKey = $"payment:reverse:{requiredOriginalTxId}:{requiredEndToEndId}:{requiredReversalTxId}";
            var idempotencyRecord = await db.IdempotencyRecords.FirstOrDefaultAsync(x => x.Key == idempotencyKey, cancellationToken);
            if (idempotencyRecord is not null && idempotencyRecord.PayloadHash != payloadHash)
            {
                return Results.Ok(ReturnResponse.Rejected(requiredOriginalTxId, requiredEndToEndId, "DUPLICATE_RETURN_DIFFERENT_PAYLOAD"));
            }

            var existing = await db.BillReversals.FirstOrDefaultAsync(
                x => x.OriginalSipsTransactionId == requiredOriginalTxId && x.ReversalTransactionId == requiredReversalTxId,
                cancellationToken);
            if (existing is not null)
            {
                if (idempotencyRecord is null)
                {
                    db.IdempotencyRecords.Add(new IdempotencyRecord
                    {
                        Id = Guid.NewGuid(),
                        Key = idempotencyKey,
                        PayloadHash = payloadHash,
                        ResourceId = existing.Id,
                        CreatedAt = clock.UtcNow
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }

                return Results.Ok(ReturnResponse.Accepted(requiredOriginalTxId, requiredEndToEndId, "Duplicate return already processed"));
            }

            var payment = await db.BillPayments.FirstOrDefaultAsync(x => x.SipsTransactionId == requiredOriginalTxId || x.TxId == requiredOriginalTxId, cancellationToken);
            if (payment is null)
            {
                return Results.Ok(ReturnResponse.Rejected(requiredOriginalTxId, requiredEndToEndId, "ORIGINAL_PAYMENT_NOT_FOUND"));
            }

            var bill = await db.Bills.FirstAsync(x => x.Id == payment.BillId, cancellationToken);
            var amount = request.Amount ?? payment.Amount;
            if (amount != payment.Amount)
            {
                return Results.Ok(ReturnResponse.Rejected(requiredOriginalTxId, requiredEndToEndId, "PARTIAL_REVERSAL_NOT_SUPPORTED"));
            }

            var now = clock.UtcNow;
            var reversal = new BillReversal
            {
                Id = Guid.NewGuid(),
                BillId = bill.Id,
                AuthorityId = bill.AuthorityId,
                OriginalSipsTransactionId = requiredOriginalTxId,
                ReversalTransactionId = requiredReversalTxId,
                Amount = amount,
                Currency = request.Currency ?? payment.Currency,
                Reason = returnReason,
                ReversedAt = request.ReversedAt ?? now,
                CreatedAt = now
            };
            db.BillReversals.Add(reversal);
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                Key = idempotencyKey,
                PayloadHash = payloadHash,
                ResourceId = reversal.Id,
                CreatedAt = now
            });

            bill.StatusProjection = BillStatuses.Reversed;
            bill.UpdatedAt = now;
            var reversedEvent = await hashChain.AppendAsync(db, bill, BillEventTypes.BillReversed, new
            {
                bill.Id,
                bill.UPR,
                originalTxId = requiredOriginalTxId,
                reversalTxId = requiredReversalTxId,
                amount,
                currency = request.Currency ?? payment.Currency
            }, "SipsConnect", request.Agent ?? "", "SipsConnect", requiredReversalTxId, cancellationToken);
            await QueueCallbacksAsync(db, bill.AuthorityId, reversedEvent, clock, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(ReturnResponse.Accepted(requiredOriginalTxId, requiredEndToEndId, "Return accepted for processing"));
        });

        group.MapPost("/payments/status", async (
            StatusRequest request,
            HttpContext httpContext,
            GatewayDbContext db,
            IOptions<P2GOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointAuth.HasInternalAccess(httpContext, options))
            {
                return Results.Unauthorized();
            }

            var txId = FirstNonEmpty(request.TxId);
            var endToEndId = FirstNonEmpty(request.EndToEndId);
            if (txId is null && endToEndId is null)
            {
                return Results.Ok(PaymentStatusResponse.NotFound(
                    request.Agent,
                    "",
                    "",
                    "MISSING_PAYMENT_REFERENCE",
                    "txId or endToEndId is required"));
            }

            var payment = await db.BillPayments.FirstOrDefaultAsync(
                x => (txId != null && (x.TxId == txId || x.SipsTransactionId == txId)) ||
                     (endToEndId != null && x.EndToEndId == endToEndId),
                cancellationToken);

            if (payment is null)
            {
                return Results.Ok(PaymentStatusResponse.NotFound(request.Agent, txId ?? "", endToEndId ?? ""));
            }

            return Results.Ok(PaymentStatusResponse.FromPayment(payment, request.Agent));
        });

        return app;
    }

    private static string? ValidateVerificationContract(VerificationRequest request)
    {
        if (FirstNonEmpty(request.InvoiceIdOrUpr, request.AccountNo) is null) return "MISSING_ACCOUNT_NO";
        if (FirstNonEmpty(request.Agent, request.PayerBankCode) is null) return "MISSING_AGENT";
        if (FirstNonEmpty(request.VerificationRequestId, request.VerificationId) is null) return "MISSING_VERIFICATION_ID";
        return null;
    }

    private static async Task<IResult> VerifyInvoiceAsync(
        VerificationRequest request,
        string lookup,
        string verificationId,
        GatewayDbContext db,
        IDescriptorService descriptorService,
        IHashChainService hashChain,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var bill = await FindBillAsync(db, lookup, cancellationToken);
        if (bill is null)
        {
            return Results.Ok(VerificationResponse.Rejected("NOT_FOUND"));
        }

        var rejectReason = GetNonPayableReason(bill, clock.UtcNow);
        if (rejectReason is not null)
        {
            return Results.Ok(VerificationResponse.Rejected(rejectReason));
        }

        var account = await db.RevenueAccounts.FirstAsync(x => x.Id == bill.RevenueAccountId, cancellationToken);
        var descriptor = descriptorService.Build(bill, account);
        var now = clock.UtcNow;

        db.BillVerificationSnapshots.Add(new BillVerificationSnapshot
        {
            Id = Guid.NewGuid(),
            BillId = bill.Id,
            AuthorityId = bill.AuthorityId,
            VerificationRequestId = verificationId,
            PayerBankCode = request.PayerBankCode ?? request.Agent ?? "",
            PayerChannel = request.PayerChannel ?? "",
            Descriptor = descriptor.Descriptor,
            DescriptorVersion = descriptor.Version,
            DescriptorHmac = descriptor.FullHmac,
            DescriptorKeyVersion = descriptor.KeyVersion,
            CreditorClearingId = account.AccountNumber,
            CreditorClearingType = account.ClearingType,
            CreditorName = account.AccountName,
            Amount = bill.Amount,
            Currency = bill.Currency,
            ExpiresAt = bill.ExpiresAt,
            CreatedAt = now
        });

        if (bill.StatusProjection == BillStatuses.Available)
        {
            bill.StatusProjection = BillStatuses.Fetched;
            bill.UpdatedAt = now;
        }

        var fetchedEvent = await hashChain.AppendAsync(db, bill, BillEventTypes.BillFetched, new
        {
            bill.Id,
            bill.UPR,
            verificationId,
            payerBankCode = request.PayerBankCode ?? request.Agent
        }, "SipsConnect", request.Agent ?? "", "SipsConnect", verificationId, cancellationToken);

        await hashChain.AppendAsync(db, bill, BillEventTypes.VerificationReturned, new
        {
            bill.Id,
            bill.UPR,
            descriptor = descriptor.Descriptor,
            account.AccountNumber,
            account.ClearingType
        }, "SipsConnect", request.Agent ?? "", "SipsConnect", verificationId, cancellationToken);

        await QueueCallbacksAsync(db, bill.AuthorityId, fetchedEvent, clock, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new VerificationResponse(
            true,
            "Invoice verified",
            account.AccountNumber,
            account.ClearingType,
            descriptor.Descriptor,
            account.AccountName,
            bill.Currency));
    }

    private static string? ValidatePaymentContract(PaymentRequest request, string? txId)
    {
        if (txId is null) return "MISSING_TX_ID";
        if (FirstNonEmpty(request.EndToEndId) is null) return "MISSING_END_TO_END_ID";
        if (FirstNonEmpty(request.FromBIC, request.Payer?.BankCode, request.DebtorAgentBIC) is null) return "MISSING_FROM_BIC";
        if (FirstNonEmpty(request.ToBIC, request.Payee?.BankCode, request.CreditorAgentBIC) is null) return "MISSING_TO_BIC";
        if (FirstNonEmpty(request.CategoryPurpose) is null) return "MISSING_CATEGORY_PURPOSE";
        if (!IsB2GCategoryPurpose(request.CategoryPurpose)) return "INVALID_CATEGORY_PURPOSE";
        if (GetPaymentAmount(request) <= 0) return "INVALID_AMOUNT";
        if (FirstNonEmpty(request.Currency) is null) return "MISSING_CURRENCY";
        if (FirstNonEmpty(request.CreditorAccount, request.Payee?.Account) is null) return "MISSING_CREDITOR_ACCOUNT";
        if (FirstNonEmpty(request.CreditorName, request.Payee?.Name) is null) return "MISSING_CREDITOR_NAME";
        if (FirstNonEmpty(request.CreditorAccountType, request.Payee?.ClearingType) is null) return "MISSING_CREDITOR_ACCOUNT_TYPE";
        if (FirstNonEmpty(request.RemittanceInformation) is null) return "MISSING_REMITTANCE_INFORMATION";
        if (FirstNonEmpty(request.MsgDefIdr) is null) return "MISSING_MSG_DEF_IDR";
        if (!request.MsgDefIdr!.StartsWith("pacs.008", StringComparison.OrdinalIgnoreCase)) return "INVALID_MSG_DEF_IDR";
        return null;
    }

    private static bool IsB2GCategoryPurpose(string? categoryPurpose)
    {
        return string.Equals(categoryPurpose, "C2GCRT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(categoryPurpose, "C2GSQR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(categoryPurpose, "C2GDQR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCompletionNotification(PaymentRequest request)
    {
        return FirstNonEmpty(request.SipsTransactionId, request.TxId) is not null
            && FirstNonEmpty(request.ToBIC, request.Payee?.BankCode, request.CreditorAgentBIC) is null
            && FirstNonEmpty(request.CategoryPurpose) is null
            && FirstNonEmpty(request.MsgDefIdr) is null
            && FirstNonEmpty(request.Status) is not null;
    }

    private static async Task<IResult> HandlePaymentCompletionAsync(
        PaymentRequest completion,
        string? txId,
        GatewayDbContext db,
        IHashChainService hashChain,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (txId is null)
        {
            return Results.Ok(PaymentResponse.Rejected(completion.EndToEndId, "", "MISSING_TX_ID"));
        }

        var existingPayment = await db.BillPayments.FirstOrDefaultAsync(x => x.SipsTransactionId == txId || x.TxId == txId, cancellationToken);
        if (existingPayment is not null)
        {
            return Results.Ok(PaymentResponse.Accepted(existingPayment.EndToEndId, txId, "Duplicate payment already processed"));
        }

        var pendingAttempt = await db.BillPaymentAttempts
            .Where(x => x.TxId == txId && x.Status == "ACSC" && x.Reason == "PAYMENT_ACCEPTED_PENDING_COMPLETION")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (pendingAttempt is null)
        {
            return Results.Ok(PaymentResponse.Rejected(completion.EndToEndId, txId, "PAYMENT_ATTEMPT_NOT_FOUND"));
        }

        if (!string.Equals(completion.Status, "ACSC", StringComparison.OrdinalIgnoreCase))
        {
            var rejectionReason = completion.Status ?? "PAYMENT_COMPLETION_REJECTED";
            await StoreAttemptAsync(db, completion, pendingAttempt.BillId, pendingAttempt.AuthorityId, completion.Status ?? "RJCT", rejectionReason, clock, cancellationToken);
            if (pendingAttempt.BillId is not null)
            {
                var failedBill = await db.Bills.FirstOrDefaultAsync(x => x.Id == pendingAttempt.BillId, cancellationToken);
                if (failedBill is not null && failedBill.StatusProjection == BillStatuses.PaymentInitiated)
                {
                    failedBill.StatusProjection = BillStatuses.Failed;
                    failedBill.UpdatedAt = clock.UtcNow;
                    await hashChain.AppendAsync(db, failedBill, BillEventTypes.PaymentRejected, new { failedBill.Id, failedBill.UPR, txId, reason = rejectionReason }, "SipsConnect", completion.FromBIC ?? "", "SipsConnect", txId, cancellationToken);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(PaymentResponse.Rejected(completion.EndToEndId ?? pendingAttempt.EndToEndId, txId, rejectionReason));
        }

        var initial = JsonSerializer.Deserialize<PaymentRequest>(pendingAttempt.RawPayloadJson, JsonDefaults.SerializerOptions);
        if (initial is null || pendingAttempt.BillId is null)
        {
            return Results.Ok(PaymentResponse.Rejected(completion.EndToEndId ?? pendingAttempt.EndToEndId, txId, "INVALID_PAYMENT_ATTEMPT"));
        }

        var bill = await db.Bills.FirstOrDefaultAsync(x => x.Id == pendingAttempt.BillId, cancellationToken);
        if (bill is null)
        {
            return Results.Ok(PaymentResponse.Rejected(completion.EndToEndId ?? pendingAttempt.EndToEndId, txId, "BILL_NOT_FOUND"));
        }

        var now = clock.UtcNow;
        var paidAt = completion.PaidAt ?? completion.AcceptedAtUtc ?? completion.Date ?? now;
        var payment = new BillPayment
        {
            Id = Guid.NewGuid(),
            BillId = bill.Id,
            AuthorityId = bill.AuthorityId,
            SipsTransactionId = txId,
            EndToEndId = FirstNonEmpty(completion.EndToEndId, initial.EndToEndId) ?? "",
            OriginalMessageId = initial.OriginalMessageId ?? initial.MsgId ?? "",
            TxId = txId,
            Amount = GetPaymentAmount(initial),
            Currency = initial.Currency ?? "",
            Status = "ACSC",
            PaidAt = paidAt,
            PayerName = initial.Payer?.Name ?? initial.DebtorName ?? "",
            PayerAccount = initial.Payer?.Account ?? initial.DebtorAccount ?? "",
            PayerBankCode = initial.Payer?.BankCode ?? initial.FromBIC ?? "",
            PayerBankName = initial.Payer?.BankName ?? "",
            PayerClearingType = initial.Payer?.ClearingType ?? initial.DebtorAccountType ?? "",
            PayeeName = initial.Payee?.Name ?? initial.CreditorName ?? "",
            PayeeAccount = initial.Payee?.Account ?? initial.CreditorAccount ?? "",
            PayeeBankCode = initial.Payee?.BankCode ?? initial.ToBIC ?? "",
            PayeeBankName = initial.Payee?.BankName ?? "",
            PayeeClearingType = initial.Payee?.ClearingType ?? initial.CreditorAccountType ?? "",
            RawPayloadJson = JsonDefaults.Serialize(new { initial, completion }),
            CreatedAt = now
        };
        db.BillPayments.Add(payment);

        bill.StatusProjection = BillStatuses.Paid;
        bill.UpdatedAt = now;

        var paidAmount = GetPaymentAmount(initial);
        var paidEvent = await hashChain.AppendAsync(db, bill, BillEventTypes.BillPaid, new { bill.Id, bill.UPR, txId, amount = paidAmount, currency = initial.Currency, paidAt }, "SipsConnect", initial.FromBIC ?? "", "SipsConnect", txId, cancellationToken);
        await QueueCallbacksAsync(db, bill.AuthorityId, paidEvent, clock, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            db.ChangeTracker.Clear();
            existingPayment = await db.BillPayments.FirstOrDefaultAsync(x => x.SipsTransactionId == txId || x.TxId == txId, cancellationToken);
            if (existingPayment is null)
            {
                throw;
            }

            return Results.Ok(PaymentResponse.Accepted(existingPayment.EndToEndId, txId, "Duplicate payment already processed"));
        }

        return Results.Ok(PaymentResponse.Accepted(payment.EndToEndId, txId, "Payment completed"));
    }

    private static string? ValidateReturnContract(ReturnRequest request, string? originalTxId, string? reversalTxId)
    {
        if (originalTxId is null) return "MISSING_ORIGINAL_TX_ID";
        if (FirstNonEmpty(request.EndToEndId) is null) return "MISSING_END_TO_END_ID";
        if (reversalTxId is null) return "MISSING_RETURN_ID";
        if (FirstNonEmpty(request.Reason) is null) return "MISSING_RETURN_REASON";
        return null;
    }

    private static string? ValidatePayment(PaymentRequest request, Bill bill, Domain.Authorities.RevenueAccount account, DateTimeOffset now)
    {
        if (bill.ExpiresAt <= now) return "EXPIRED";
        if (bill.StatusProjection == BillStatuses.Cancelled) return "CANCELLED";
        if (bill.StatusProjection == BillStatuses.Voided) return "VOIDED";
        if (bill.StatusProjection == BillStatuses.PaymentInitiated) return "PAYMENT_IN_PROGRESS";
        if (bill.StatusProjection == BillStatuses.Paid) return "ALREADY_PAID";
        if (bill.StatusProjection == BillStatuses.Reversed) return "REVERSED";
        if (GetPaymentAmount(request) != bill.Amount) return "AMOUNT_MISMATCH";
        if (!string.Equals(request.Currency, bill.Currency, StringComparison.OrdinalIgnoreCase)) return "CURRENCY_MISMATCH";
        var creditorAccount = request.Payee?.Account ?? request.CreditorAccount;
        if (!string.Equals(creditorAccount, account.AccountNumber, StringComparison.OrdinalIgnoreCase)) return "CREDITOR_ACCOUNT_MISMATCH";
        return null;
    }

    private static decimal GetPaymentAmount(PaymentRequest request) => request.Amount > 0 ? request.Amount : request.AmountPayable ?? 0m;

    private static bool IsBillLookupType(string value) =>
        string.Equals(value, "BILL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "INVOICE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "UPR", StringComparison.OrdinalIgnoreCase);

    private static string? GetNonPayableReason(Bill bill, DateTimeOffset now)
    {
        if (bill.ExpiresAt <= now) return "EXPIRED";
        return bill.StatusProjection switch
        {
            BillStatuses.Cancelled => "CANCELLED",
            BillStatuses.Voided => "VOIDED",
            BillStatuses.Paid => "ALREADY_PAID",
            BillStatuses.Reversed => "REVERSED",
            _ => null
        };
    }

    private static async Task<Bill?> FindBillAsync(GatewayDbContext db, string lookup, CancellationToken cancellationToken)
    {
        lookup = lookup.Trim();
        var billByGuid = Guid.TryParse(lookup, out var billId);
        return await db.Bills.FirstOrDefaultAsync(
            x => x.UPR == lookup || x.ExternalInvoiceId == lookup || (billByGuid && x.Id == billId),
            cancellationToken);
    }

    private static async Task<Domain.Authorities.RevenueAccount?> FindRevenueAccountAsync(
        GatewayDbContext db,
        string accountNo,
        string accountType,
        CancellationToken cancellationToken)
    {
        var normalizedAccountNo = accountNo.Trim();
        var normalizedAccountType = accountType.Trim().ToUpperInvariant();
        var exactMatch = await db.RevenueAccounts
            .Where(x => x.AccountNumber == normalizedAccountNo &&
                        x.ClearingType == normalizedAccountType &&
                        x.Status == "Active")
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.MdaCode)
            .ThenBy(x => x.ServiceCode)
            .FirstOrDefaultAsync(cancellationToken);

        if (exactMatch is not null || !string.Equals(normalizedAccountType, "ACCT", StringComparison.OrdinalIgnoreCase))
        {
            return exactMatch;
        }

        return await db.RevenueAccounts
            .Where(x => x.AccountNumber == normalizedAccountNo &&
                        x.Status == "Active")
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.MdaCode)
            .ThenBy(x => x.ServiceCode)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string? ParseBillReference(string? remittanceInformation)
    {
        if (string.IsNullOrWhiteSpace(remittanceInformation))
        {
            return null;
        }

        var value = remittanceInformation.Trim();
        return value.StartsWith("BILL:", StringComparison.OrdinalIgnoreCase) ? value[5..].Trim() : value;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    private static async Task<PaymentResponse?> TryBuildDuplicatePaymentResponseAsync(
        GatewayDbContext db,
        string paymentTxId,
        string payloadHash,
        string paymentEndToEndId,
        CancellationToken cancellationToken)
    {
        var duplicatePayment = await db.BillPayments.FirstOrDefaultAsync(
            x => x.SipsTransactionId == paymentTxId || x.TxId == paymentTxId,
            cancellationToken);
        return duplicatePayment is null
            ? null
            : BuildDuplicatePaymentResponse(duplicatePayment, paymentTxId, payloadHash, paymentEndToEndId);
    }

    private static PaymentResponse BuildDuplicatePaymentResponse(BillPayment duplicatePayment, string paymentTxId, string payloadHash, string paymentEndToEndId)
    {
        return JsonDefaults.Sha256(duplicatePayment.RawPayloadJson) == payloadHash
            ? PaymentResponse.Accepted(duplicatePayment.EndToEndId, paymentTxId, "Duplicate payment already processed")
            : PaymentResponse.Rejected(paymentEndToEndId, paymentTxId, "DUPLICATE_TX_DIFFERENT_PAYLOAD");
    }

    private static async Task<PaymentResponse?> TryBuildPendingPaymentResponseAsync(
        GatewayDbContext db,
        string paymentTxId,
        string payloadHash,
        string paymentEndToEndId,
        CancellationToken cancellationToken)
    {
        var pendingAttempt = await db.BillPaymentAttempts
            .Where(x => x.TxId == paymentTxId && x.Status == "ACSC" && x.Reason == "PAYMENT_ACCEPTED_PENDING_COMPLETION")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (pendingAttempt is null)
        {
            return null;
        }

        return JsonDefaults.Sha256(pendingAttempt.RawPayloadJson) == payloadHash
            ? PaymentResponse.Accepted(pendingAttempt.EndToEndId, paymentTxId, "Duplicate payment accepted; awaiting completion notification")
            : PaymentResponse.Rejected(paymentEndToEndId, paymentTxId, "DUPLICATE_TX_DIFFERENT_PAYLOAD");
    }

    private static Task StoreAttemptAsync(
        GatewayDbContext db,
        PaymentRequest request,
        Guid? billId,
        Guid? authorityId,
        string status,
        string reason,
        IClock clock,
        CancellationToken cancellationToken)
    {
        db.BillPaymentAttempts.Add(new BillPaymentAttempt
        {
            Id = Guid.NewGuid(),
            BillId = billId,
            AuthorityId = authorityId,
            TxId = request.TxId ?? request.SipsTransactionId ?? "",
            EndToEndId = request.EndToEndId ?? "",
            Status = status,
            Reason = reason,
            RawPayloadJson = JsonDefaults.Serialize(request),
            CreatedAt = clock.UtcNow
        });
        return Task.CompletedTask;
    }

    private static async Task QueueCallbacksAsync(GatewayDbContext db, Guid authorityId, BillEvent evt, IClock clock, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var subscriptions = await db.CallbackSubscriptions
            .Where(x => x.AuthorityId == authorityId && x.Enabled)
            .ToListAsync(cancellationToken);

        foreach (var subscription in subscriptions)
        {
            var subscribedEvents = JsonSerializer.Deserialize<string[]>(subscription.SubscribedEventsJson, JsonDefaults.SerializerOptions) ?? [];
            if (!subscribedEvents.Contains(evt.EventType, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var payloadJson = JsonDefaults.Serialize(new
            {
                eventId = evt.Id,
                eventType = evt.EventType,
                billId = evt.BillId,
                authorityId,
                eventTime = evt.EventTime
            });

            var delivery = new CallbackDelivery
            {
                Id = Guid.NewGuid(),
                AuthorityId = authorityId,
                EventId = evt.Id,
                CallbackSubscriptionId = subscription.Id,
                Url = subscription.CallbackUrl,
                PayloadJson = payloadJson,
                Signature = "",
                Status = "Pending",
                AttemptCount = 0,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.CallbackDeliveries.Add(delivery);
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = $"Callback:{evt.EventType}",
                AggregateId = delivery.Id,
                PayloadJson = payloadJson,
                Status = "Pending",
                Attempts = 0,
                NextAttemptAt = now,
                CreatedAt = now
            });
        }
    }
}

public sealed record VerificationRequest(
    string? AccountNo,
    string? AccountType,
    string? Type,
    string? Agent,
    string? VerificationId,
    string? VerificationRequestId,
    string? InvoiceIdOrUpr,
    string? PayerBankCode,
    string? PayerChannel,
    string? PayerReference);

public sealed record VerificationResponse(
    bool IsVerified,
    string Message,
    string AccountNo,
    string AccountType,
    string Name,
    string Address,
    string Currency)
{
    public static VerificationResponse Rejected(string message) => new(false, message, "", "", "", "", "");
}

public sealed record PaymentRequest(
    string? FromBIC,
    string? ToBIC,
    string? LocalInstrument,
    string? CategoryPurpose,
    string? SipsTransactionId,
    string? TxId,
    string? EndToEndId,
    string? OriginalMessageId,
    string? MsgId,
    string? Upr,
    string? InvoiceId,
    string? BillReference,
    decimal Amount,
    decimal? AmountPayable,
    string Currency,
    string? DebtorName,
    string? DebtorAccount,
    string? DebtorAddress,
    string? DebtorAccountType,
    string? DebtorAgentBIC,
    string? CreditorName,
    string? CreditorAccount,
    string? CreditorAddress,
    string? CreditorAccountType,
    string? CreditorAgentBIC,
    string? CreditorIssuer,
    string? RemittanceInformation,
    DateTimeOffset? Date,
    string? SettlementMethod,
    string? ChargeBearer,
    string? BizMsgIdr,
    string? MsgDefIdr,
    string? ClearingSystem,
    string? Status,
    DateTimeOffset? PaidAt,
    DateTimeOffset? AcceptedAtUtc,
    PaymentParty? Payer,
    PaymentParty? Payee);

public sealed record PaymentParty(
    string? Name,
    string? Account,
    string? BankCode,
    string? BankName,
    string? ClearingType);

public sealed record PaymentResponse(
    string? EndToEndId,
    string Status,
    string Reason,
    string AdditionalInfo,
    DateTimeOffset AcceptanceDate,
    DateTimeOffset AcceptedAtUtc,
    string TxId)
{
    public static PaymentResponse Accepted(string? endToEndId, string txId, string additionalInfo) =>
        new(endToEndId, "ACSC", "Accepted", additionalInfo, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, txId);

    public static PaymentResponse Rejected(string? endToEndId, string txId, string reason) =>
        new(endToEndId, "RJCT", reason, reason, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, txId);
}

public sealed record ReturnRequest(
    string? OriginalSipsTransactionId,
    string? ReversalTransactionId,
    string? TxId,
    string? EndToEndId,
    string? ReturnId,
    string? Agent,
    string? Reason,
    string? AdditionalInfo,
    decimal? Amount,
    string? Currency,
    DateTimeOffset? ReversedAt);

public sealed record ReturnResponse(
    string TxId,
    string? EndToEndId,
    string Status,
    string Reason,
    string AdditionalInfo)
{
    public static ReturnResponse Accepted(string txId, string? endToEndId, string info) => new(txId, endToEndId, "ACSC", "Accepted", info);
    public static ReturnResponse Rejected(string txId, string? endToEndId, string reason) => new(txId, endToEndId, "RJCT", reason, reason);
}

public sealed record StatusRequest(
    string? EndToEndId,
    string? TxId,
    string? Agent);

public sealed record PaymentStatusResponse(
    string FromBIC,
    string ToBIC,
    string LocalInstrument,
    string CategoryPurpose,
    string TxId,
    string EndToEndId,
    decimal Amount,
    string Currency,
    string DebtorName,
    string DebtorAccount,
    string DebtorPostalAddress,
    string DebtorAccountType,
    string DebtorAgentBIC,
    string DebtorIssuer,
    string CreditorName,
    string CreditorPostalAddress,
    string CreditorAccount,
    string CreditorAccountType,
    string CreditorAgentBIC,
    string CreditorIssuer,
    string RemittanceInformation,
    DateTimeOffset Date,
    string SettlementMethod,
    string ChargeBearer,
    string Status,
    string Reason,
    string AdditionalInfo,
    DateTimeOffset AcceptanceDate,
    DateTimeOffset AcceptedAtUtc,
    string BizMsgIdr,
    string MsgDefIdr,
    string ClearingSystem,
    string MsgId)
{
    public static PaymentStatusResponse FromPayment(BillPayment payment, string? requestingAgent) => new(
        FromBIC: payment.PayerBankCode,
        ToBIC: payment.PayeeBankCode,
        LocalInstrument: "",
        CategoryPurpose: "C2GCRT",
        TxId: payment.TxId,
        EndToEndId: payment.EndToEndId,
        Amount: payment.Amount,
        Currency: payment.Currency,
        DebtorName: payment.PayerName,
        DebtorAccount: payment.PayerAccount,
        DebtorPostalAddress: "",
        DebtorAccountType: payment.PayerClearingType,
        DebtorAgentBIC: payment.PayerBankCode,
        DebtorIssuer: "",
        CreditorName: payment.PayeeName,
        CreditorPostalAddress: "",
        CreditorAccount: payment.PayeeAccount,
        CreditorAccountType: payment.PayeeClearingType,
        CreditorAgentBIC: payment.PayeeBankCode,
        CreditorIssuer: "",
        RemittanceInformation: $"BILL:{payment.EndToEndId}",
        Date: payment.PaidAt,
        SettlementMethod: "CLRG",
        ChargeBearer: "SLEV",
        Status: payment.Status,
        Reason: "Accepted",
        AdditionalInfo: "Invoice payment posted",
        AcceptanceDate: payment.PaidAt,
        AcceptedAtUtc: payment.PaidAt,
        BizMsgIdr: "",
        MsgDefIdr: "pacs.008",
        ClearingSystem: "SIPS",
        MsgId: payment.OriginalMessageId);

    public static PaymentStatusResponse NotFound(
        string? requestingAgent,
        string txId,
        string endToEndId,
        string reason = "NOT_FOUND",
        string additionalInfo = "Payment was not found") => new(
        FromBIC: requestingAgent ?? "",
        ToBIC: "",
        LocalInstrument: "",
        CategoryPurpose: "C2GCRT",
        TxId: txId,
        EndToEndId: endToEndId,
        Amount: 0,
        Currency: "",
        DebtorName: "",
        DebtorAccount: "",
        DebtorPostalAddress: "",
        DebtorAccountType: "",
        DebtorAgentBIC: requestingAgent ?? "",
        DebtorIssuer: "",
        CreditorName: "",
        CreditorPostalAddress: "",
        CreditorAccount: "",
        CreditorAccountType: "",
        CreditorAgentBIC: "",
        CreditorIssuer: "",
        RemittanceInformation: "",
        Date: DateTimeOffset.UtcNow,
        SettlementMethod: "",
        ChargeBearer: "",
        Status: "RJCT",
        Reason: reason,
        AdditionalInfo: additionalInfo,
        AcceptanceDate: DateTimeOffset.UtcNow,
        AcceptedAtUtc: DateTimeOffset.UtcNow,
        BizMsgIdr: "",
        MsgDefIdr: "pacs.008",
        ClearingSystem: "SIPS",
        MsgId: "");
}
