---
layout: default
title: Registo Dinâmico de Clientes
locale: pt
---

# Registo Dinâmico de Clientes

O Authagonal implementa o **Registo Dinâmico de Clientes OAuth 2.0** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), permitindo que aplicações cliente se registem em tempo de execução sem envolvimento do administrador.

## Habilitando o Endpoint

O registo dinâmico está **desabilitado por padrão**. Opte por ativá-lo via configuração:

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

Ou defina `Auth__DynamicClientRegistrationEnabled=true` como uma variável de ambiente.

Quando habilitado, o documento de descoberta anuncia o endpoint:

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Registando um Cliente

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Resposta

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

O `client_secret` é retornado **uma vez** e não pode ser recuperado depois. Armazene-o de forma segura.

## Parâmetros da Requisição

| Parâmetro | Obrigatório | Notas |
|---|---|---|
| `client_name` | não | Assume por padrão o `client_id` gerado, se omitido |
| `redirect_uris` | condicional | Obrigatório quando `grant_types` contém `authorization_code`. Devem ser URIs absolutos; os esquemas `javascript:`/`data:`/`vbscript:`/`file:` são rejeitados (esquemas personalizados nativos para deep links móveis são aceites). |
| `post_logout_redirect_uris` | não | Alvos de redirecionamento válidos após o logout |
| `grant_types` | não | Assume por padrão `["authorization_code"]`. **Apenas `authorization_code` e `refresh_token` são registáveis**: `client_credentials`, `implicit`, device e qualquer outro tipo de concessão são rejeitados com `invalid_client_metadata`, portanto o registo aberto nunca pode cunhar um cliente máquina-a-máquina. `refresh_token` é adicionado automaticamente se `offline_access` for pedido. |
| `token_endpoint_auth_method` | não | `client_secret_basic` (padrão), `client_secret_post`, ou `none` para clientes públicos |
| `scope` | não | Scopes separados por espaços: todos devem ser integrados ou previamente registados (consulte [Scopes](scopes)). O scope administrativo (`AdminApi:Scope`, padrão `authagonal-admin`) nunca pode ser registado. |
| `audiences` | não | Valores `aud` de JWT adicionados aos access tokens |
| `allowed_cors_origins` | não | Origens autorizadas a chamar o endpoint de token a partir de um navegador |
| `backchannel_logout_uri` | não | Habilita o [Logout Back-Channel](index#features) |
| `frontchannel_logout_uri` | não | Habilita o [Logout Front-Channel](front-channel-logout) |
| `frontchannel_logout_session_required` | não | Assume por padrão `true`; quando `true`, a URL de logout carrega os parâmetros `iss` e `sid` |

## Padrões e Invariantes

- **PKCE obrigatório**: `RequirePkce` é sempre `true` para clientes registados dinamicamente.
- **Clientes públicos**: `token_endpoint_auth_method: "none"` produz um cliente sem segredo. O PKCE continua a ser obrigatório.
- **Acesso offline**: pedir o scope `offline_access` adiciona implicitamente `refresh_token` a `grant_types`.

## Respostas de Erro

| HTTP | `error` | Causa |
|---|---|---|
| `400` | `invalid_redirect_uri` | Um dos `redirect_uris` não é um URI absoluto válido, ou usa um pseudo-esquema script/data/file |
| `400` | `invalid_client_metadata` | Foi pedido um tipo de concessão não registável, ou faltam os `redirect_uris` para um tipo de concessão que os exige |
| `400` | `invalid_scope` | Um scope pedido não é integrado nem registado |
| `403` | `invalid_scope` | O scope administrativo foi pedido: ele nunca pode ser concedido através do registo |
| `403` | `not_supported` | O registo dinâmico de clientes não está habilitado |
| `429` | `rate_limited` | Demasiados registos a partir deste IP (10 por hora) |

## Considerações de Segurança

O endpoint de registo é **não autenticado**, mas restringido por design:

- **Limitação de taxa**: 10 registos por IP por hora deslizante (`429 rate_limited`), para que o store de clientes não possa ser inundado.
- **Tipos de concessão restritos**: apenas `authorization_code` + `refresh_token`; um cliente registado exige sempre um fluxo mediado por utilizador e nunca pode atuar como um cliente máquina-a-máquina.
- **Scope de administração reservado**: o scope `authagonal-admin` (ou qualquer que seja o valor de `AdminApi:Scope`) é recusado, portanto o registo nunca pode produzir um cliente que alcance a [API de administração](admin-api).
- **PKCE sempre obrigatório** nos clientes registados.

Para um controlo mais forte (initial access tokens, mTLS, software statements), coloque o seu próprio middleware ou um `IAuthHook` à frente do endpoint. Considere desabilitar o registo dinâmico por completo e gerir os clientes via a API de administração em ambientes onde o registo self-service não é um requisito.
