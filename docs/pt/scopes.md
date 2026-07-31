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
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
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
| `Group` | Cabeçalho da tela de consentimento sob o qual arquivar este scope. Apenas apresentação -- nunca afeta o que é concedido |
| `Required` | Se `true`, o utilizador não pode desmarcar este scope ao consentir |
| `ShowInDiscoveryDocument` | Se `true`, o scope aparece em `/.well-known/openid-configuration` sob `scopes_supported` |
| `AllowedRoles` | Papéis que um utilizador precisa deter para que este scope lhe seja concedido. Vazio (o padrão) deixa-o sem restrição -- consulte [Scopes restringidos por papel](#role-gated-scopes) |
| `UserClaims` | Claims adicionadas ao access token quando este scope é concedido |

### Scopes restringidos por papel {#role-gated-scopes}

Os `AllowedScopes` de um cliente respondem *pode esta aplicação pedir este scope* -- uma questão
resolvida antes de alguém ter iniciado sessão. `AllowedRoles` responde à outra metade: *pode esta
pessoa tê-lo*. Ambas as barreiras se aplicam, e nenhuma substitui a outra.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

A um utilizador que não detenha nenhum dos papéis listados o scope é **retirado da concessão**, não
recusado: o cliente pediu o seu conjunto completo e é informado, através do `scope` devolvido na
resposta do token (RFC 6749 §3.3), de que recebeu menos. É isto que permite a uma aplicação servir
tanto o pessoal interno como toda a gente -- a superfície do pessoal é um scope entre vários, e só as
pessoas com direito a ele o recebem.

Um pedido em que *todos* os scopes pedidos são retirados falha com `access_denied`, porque não sobra
nada para o qual emitir um token.

A barreira aplica-se em todo o lado onde um token é emitido para um humano:

| Fluxo | Onde corre |
|---|---|
| Authorization code | Em `/connect/authorize`, assim que o utilizador é conhecido e **antes** do consentimento -- assim a tela nunca oferece uma permissão que não pode ser concedida |
| Device code | Em `/api/auth/device/approve`, o primeiro ponto desse fluxo em que o sujeito é conhecido |
| Refresh | Em cada rotação, contra papéis resolvidos de novo. É aqui que revogar um papel produz efeito real, já que a concessão continua a registar o que foi aprovado no login |
| Token exchange | Não é restringido separadamente: uma troca só pode reduzir o âmbito dentro dos scopes do próprio subject token, pelo que nunca pode alcançar um que o sujeito não tenha recebido |

As concessões client_credentials não têm sujeito e ficam deliberadamente intocadas -- a autoridade de
um cliente de máquina é o seu registo.

Semear um scope a partir da configuração pode acrescentar ou alterar `AllowedRoles` mas não pode
limpá-lo (tal como com `UserClaims`, um campo omitido preserva o valor guardado). Para remover uma
restrição, faça `PUT` do scope com um array explicitamente vazio.

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
