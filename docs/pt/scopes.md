---
layout: default
title: Scopes OAuth
locale: pt
---

# Scopes OAuth

O Authagonal suporta tanto scopes OAuth/OIDC **integrados** quanto scopes **personalizados** geridos em tempo de execução. Os scopes personalizados são persistidos, anunciados via o documento de descoberta e apresentados na tela de consentimento ao lado dos integrados.

## Scopes Integrados

Estes scopes estão sempre disponíveis e não precisam de ser registados:

| Scope | Finalidade |
|---|---|
| `openid` | Necessário para iniciar um fluxo OIDC. Emite um ID token. |
| `profile` | Claims de perfil padrão (name, family_name, given_name, etc.) |
| `email` | Endereço de e-mail e claims `email_verified` |
| `offline_access` | Emite um refresh token junto ao access token |

## Scopes Personalizados

Os scopes personalizados são geridos através da API de administração em `/api/v1/scopes`. Requerem um JWT access token com o scope `authagonal-admin` (configurável via `AdminApi:Scope`).

### Modelo de Scope

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Campo | Descrição |
|---|---|
| `Name` | O identificador do scope enviado nas requisições de token (por exemplo, `billing.read`) |
| `DisplayName` | Nome legível apresentado na tela de consentimento |
| `Description` | Descrição mais longa apresentada na tela de consentimento |
| `Emphasize` | Se `true`, a tela de consentimento realça este scope como sensível |
| `Required` | Se `true`, o utilizador não pode desmarcar este scope ao consentir |
| `ShowInDiscoveryDocument` | Se `true`, o scope aparece em `/.well-known/openid-configuration` sob `scopes_supported` |
| `UserClaims` | Claims adicionadas ao access token quando este scope é concedido |

## Endpoints de Administração

### Listar Scopes

```
GET /api/v1/scopes
```

Retorna `{ "scopes": [ ... ] }`.

### Obter Scope

```
GET /api/v1/scopes/{name}
```

Retorna o scope ou `404` se não for encontrado.

### Criar Scope

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Retorna `201 Created` com o scope. Retorna `409` se já existir um scope com o mesmo nome.

### Atualizar Scope

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing — read",
  "description": "View invoices",
  "emphasize": true
}
```

Apenas os campos fornecidos são atualizados; os campos omitidos mantêm os seus valores atuais.

### Eliminar Scope

```
DELETE /api/v1/scopes/{name}
```

Retorna `204 No Content` (`404` se o scope não existir). Os tokens já emitidos que incluam este scope permanecem válidos até expirarem: revogue-os explicitamente via `/connect/revocation` se necessário.

## Documento de Descoberta

Os scopes com `ShowInDiscoveryDocument = true` aparecem sob `scopes_supported` em `/.well-known/openid-configuration`. Os scopes integrados são sempre anunciados.

```json
{
  "scopes_supported": ["openid", "profile", "email", "offline_access", "billing.read"]
}
```

## Tela de Consentimento

Quando um cliente pede um scope que não está na sua lista de consent-skip, a página de consentimento lista cada scope pedido pelo `DisplayName` (recorrendo a `Name`) com a `Description` por baixo. Os scopes com `Emphasize = true` recebem um tratamento visual distinto. Os scopes `Required` não podem ser desmarcados.

Consulte [Tela de Consentimento OAuth](index#features) para o fluxo voltado ao utilizador.

## Registo Dinâmico de Clientes

Os clientes registados via [Registo Dinâmico de Clientes](client-registration) só podem pedir scopes que sejam integrados ou previamente criados via a API de administração. Scopes desconhecidos são rejeitados com `invalid_scope`.
