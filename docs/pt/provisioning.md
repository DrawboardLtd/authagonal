---
layout: default
title: Provisionamento
locale: pt
---

# Provisionamento TCC

O Authagonal provisiona utilizadores em aplicações downstream usando o padrão **Try-Confirm-Cancel (TCC)**. Isto garante que todas as aplicações concordem antes que um utilizador obtenha acesso, com rollback limpo se alguma aplicação rejeitar.

## Quando o Provisionamento é Executado

O provisionamento é executado no **endpoint de autorização** (`/connect/authorize`), após o utilizador ser autenticado mas antes de um código de autorização ser emitido. Isto significa:

- É executado no primeiro login do utilizador através de um cliente que requer provisionamento
- Combinações aplicação/utilizador já provisionadas são ignoradas (rastreadas na tabela `UserProvisions`)
- Se o provisionamento falhar, o pedido de autorização retorna `access_denied` — nenhum código é emitido

## Configuração

### 1. Definir Aplicações de Provisionamento

No `appsettings.json`:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token"
    }
  }
}
```

### 2. Atribuir Aplicações a Clientes

Cada cliente declara em quais aplicações os seus utilizadores devem ser provisionados:

```json
{
  "Clients": [
    {
      "ClientId": "web-app",
      "ProvisioningApps": ["my-backend"],
      ...
    }
  ]
}
```

Quando um utilizador autoriza através de `web-app`, é provisionado no `my-backend` se ainda não tiver sido.

## Protocolo TCC

O Authagonal faz três tipos de chamadas HTTP ao seu endpoint de provisionamento. Todas usam `POST` com corpos JSON e `Authorization: Bearer {ApiKey}`.

### Fase 1: Try

**Pedido:** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null"
}
```

**Respostas esperadas:**

| Estado | Corpo | Significado |
|---|---|---|
| `200` | `{ "approved": true }` | O utilizador pode ser provisionado. A aplicação cria um registo **pendente**. |
| `200` | `{ "approved": false, "reason": "..." }` | O utilizador é rejeitado. Nenhum registo criado. |
| Não-2xx | Qualquer | Tratado como falha. |

O `transactionId` identifica esta tentativa de provisionamento. A sua aplicação deve armazená-lo junto com o registo pendente.

### Fase 2: Confirm

Chamado apenas se **todas** as aplicações retornaram `approved: true` na fase try.

**Pedido:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Resposta esperada:** `200` (qualquer corpo). A sua aplicação promove o registo pendente para confirmado.

### Fase 3: Cancel

Chamado se o try de **qualquer** aplicação foi rejeitado ou falhou, para limpar as aplicações que tiveram sucesso na fase try.

**Pedido:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Resposta esperada:** `200` (qualquer corpo). A sua aplicação elimina o registo pendente.

O cancel é feito com melhor esforço — se falhar, o Authagonal regista o erro e continua. A sua aplicação deve **recolher registos não confirmados após um TTL** (ex.: 1 hora) como rede de segurança.

## Diagrama de Fluxo

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### Em Caso de Falha

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### Em Caso de Falha Parcial de Confirmação

Se algumas confirmações tiverem sucesso mas uma falhar, as aplicações confirmadas com sucesso têm os seus registos de provisionamento armazenados (para que não sejam tentadas novamente). O utilizador vê um erro e pode tentar novamente — apenas a aplicação que falhou será tentada da próxima vez.

## Desprovisionamento

Quando um utilizador é eliminado via a API de administração (`DELETE /api/v1/profile/{userId}`), o Authagonal chama `DELETE {CallbackUrl}/users/{userId}` em cada aplicação na qual o utilizador foi provisionado. Isto é feito com melhor esforço — falhas são registadas mas não bloqueiam a eliminação.

## Implementação dos Endpoints Upstream

### Exemplo Mínimo (Node.js/Express)

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
