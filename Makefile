SHELL := /bin/bash

SOLUTION := Sps.P2G.sln
PROJECT := src/Sps.P2G.Gateway/Sps.P2G.Gateway.csproj
TEST_PROJECT := tests/Sps.P2G.Gateway.Tests/Sps.P2G.Gateway.Tests.csproj
STARTUP_PROJECT := $(PROJECT)
CONFIGURATION ?= Debug
ASPNETCORE_ENVIRONMENT ?= Development
GATEWAY_URL ?= http://localhost:8081
OPENAPI_PORT ?= 5088
OPENAPI_OUT_DIR ?= artifacts/openapi
MIGRATION_NAME ?=
MIGRATION_OUTPUT ?= artifacts/sql/migrations.sql
DOTNET_EF := dotnet tool run dotnet-ef --

.DEFAULT_GOAL := help

.PHONY: help
help:
	@awk 'BEGIN {FS = ":.*##"; printf "\nSPS P2G Gateway commands:\n\n"} /^[a-zA-Z0-9_.-]+:.*##/ {printf "  \033[36m%-24s\033[0m %s\n", $$1, $$2} END {printf "\n"}' $(MAKEFILE_LIST)

.PHONY: tool-restore
tool-restore: ## Restore local .NET tools, including dotnet-ef.
	dotnet tool restore

.PHONY: restore
restore: tool-restore ## Restore NuGet packages and local tools.
	dotnet restore $(SOLUTION)

.PHONY: build
build: restore ## Restore and build the solution.
	dotnet build $(SOLUTION) --configuration $(CONFIGURATION) --no-restore

.PHONY: rebuild
rebuild: restore ## Restore and build from a clean incremental state.
	dotnet build $(SOLUTION) --configuration $(CONFIGURATION) --no-incremental

.PHONY: test
test: build ## Build and run the full test suite.
	dotnet test $(SOLUTION) --configuration $(CONFIGURATION) --no-build --no-restore

.PHONY: test-filter
test-filter: ## Run tests matching FILTER='FullyQualifiedName~Name'.
	@if [[ -z "$(FILTER)" ]]; then echo "Set FILTER, for example: make test-filter FILTER='FullyQualifiedName~P2GFlowTests'"; exit 2; fi
	dotnet test $(TEST_PROJECT) --configuration $(CONFIGURATION) --no-build --no-restore --filter "$(FILTER)"

.PHONY: verify
verify: test ## Restore, build, and run tests.

.PHONY: format
format: restore ## Format the solution.
	dotnet format $(SOLUTION) --no-restore

.PHONY: clean
clean: ## Remove build outputs.
	dotnet clean $(SOLUTION)
	rm -rf artifacts

.PHONY: run
run: ## Run the gateway locally.
	ASPNETCORE_ENVIRONMENT=$(ASPNETCORE_ENVIRONMENT) dotnet run --project $(PROJECT)

.PHONY: watch
watch: ## Run the gateway with dotnet watch.
	ASPNETCORE_ENVIRONMENT=$(ASPNETCORE_ENVIRONMENT) dotnet watch --project $(PROJECT) run

.PHONY: deps-up
deps-up: ## Start local dependencies: Postgres and Keycloak.
	docker compose up -d postgres keycloak

.PHONY: deps-down
deps-down: ## Stop local dependencies.
	docker compose stop postgres keycloak

.PHONY: stack-up
stack-up: ## Build and start the full local stack.
	docker compose up --build -d

.PHONY: stack-down
stack-down: ## Stop and remove the local stack.
	docker compose down

.PHONY: stack-logs
stack-logs: ## Tail logs for the local stack. Override SERVICE=gateway/postgres/keycloak.
	docker compose logs -f $(SERVICE)

.PHONY: docker-build
docker-build: ## Build the gateway Docker image.
	docker build -t sps-p2g-gateway:local .

.PHONY: db-migrate
db-migrate: tool-restore ## Apply EF Core migrations to the configured database.
	$(DOTNET_EF) database update --project $(PROJECT) --startup-project $(STARTUP_PROJECT)

.PHONY: db-drop
db-drop: tool-restore ## Drop the configured development database.
	$(DOTNET_EF) database drop --force --project $(PROJECT) --startup-project $(STARTUP_PROJECT)

.PHONY: db-migration-add
db-migration-add: tool-restore ## Add an EF migration. Usage: make db-migration-add MIGRATION_NAME=Name
	@if [[ -z "$(MIGRATION_NAME)" ]]; then echo "Set MIGRATION_NAME, for example: make db-migration-add MIGRATION_NAME=AddPaymentsIndex"; exit 2; fi
	$(DOTNET_EF) migrations add $(MIGRATION_NAME) --project $(PROJECT) --startup-project $(STARTUP_PROJECT) --output-dir Infrastructure/Database/Migrations

.PHONY: db-migration-remove
db-migration-remove: tool-restore ## Remove the latest EF migration.
	$(DOTNET_EF) migrations remove --project $(PROJECT) --startup-project $(STARTUP_PROJECT)

.PHONY: db-migration-list
db-migration-list: tool-restore ## List EF migrations.
	$(DOTNET_EF) migrations list --project $(PROJECT) --startup-project $(STARTUP_PROJECT)

.PHONY: db-script
db-script: tool-restore ## Generate an idempotent SQL migration script.
	mkdir -p $(dir $(MIGRATION_OUTPUT))
	$(DOTNET_EF) migrations script --idempotent --project $(PROJECT) --startup-project $(STARTUP_PROJECT) --output $(MIGRATION_OUTPUT)

.PHONY: openapi
openapi: build ## Generate OpenAPI JSON and YAML under artifacts/openapi.
	OPENAPI_PORT=$(OPENAPI_PORT) OPENAPI_OUT_DIR=$(OPENAPI_OUT_DIR) ./scripts/export-openapi.py

.PHONY: openapi-json
openapi-json: openapi ## Generate OpenAPI JSON under artifacts/openapi.

.PHONY: openapi-yaml
openapi-yaml: openapi ## Generate OpenAPI YAML under artifacts/openapi.

.PHONY: open
open: ## Open the local gateway OpenAPI document in a browser.
	open "$(GATEWAY_URL)/openapi/v1.json"
