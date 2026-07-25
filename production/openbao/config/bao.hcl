ui = true
disable_mlock = true

storage "file" {
  path = "/bao/data"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = 1
}

api_addr = "https://127.0.0.1:9443"
cluster_addr = "https://127.0.0.1:9443"
