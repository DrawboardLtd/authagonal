---
layout: default
title: Início Rápido
locale: pt
---

# Início Rápido

Coloque o Authagonal em execução localmente em 5 minutos.

## 1. Iniciar o Servidor

```bash
docker compose up
```

Isto inicia o Authagonal em `http://localhost:8080` com o Azurite para armazenamento.

## 2. Verificar se Está em Execução

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Registar um Cliente

Adicione um cliente ao seu `appsettings.json` (ou passe via variáveis de ambiente):

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Os clientes são semeados na inicialização — seguro executar em cada implantação.

## 4. Iniciar um Login

Redirecione os seus utilizadores para:

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

O utilizador vê a página de login, autentica-se e é redirecionado de volta com um código de autorização.

## 5. Trocar o Código

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Resposta:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800
}
```

## Demo Funcional

O diretório `demos/sample-app/` contém um SPA React completo + API que implementa o fluxo OIDC completo acima. Consulte o [README dos demos](https://github.com/authagonal/authagonal/tree/master/demos) para instruções.

## Próximos Passos

- [Configuração](configuration) — referência completa de todas as definições
- [Extensibilidade](extensibility) — hospedar como biblioteca, adicionar hooks personalizados
- [Personalização Visual](branding) — personalizar a interface de login
- [SAML](saml) — adicionar provedores SSO SAML
- [Provisionamento](provisioning) — provisionar utilizadores em aplicações downstream
