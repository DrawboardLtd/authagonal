---
layout: default
title: Pushed Authorization Requests
locale: pt
---

# Pushed Authorization Requests (PAR)

O [RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) permite que um cliente envie via POST os seus parâmetros de authorize-request diretamente ao servidor com autenticação de cliente padrão e receba um `request_uri` opaco de curta duração para entregar ao navegador. O navegador então visita `/connect/authorize?request_uri=...&client_id=...` em vez de carregar cada parâmetro na URL.

Por que usar:

- Os parâmetros de authorize nunca aparecem no histórico do navegador, nos logs do servidor ou em cabeçalhos `Referer`.
- O servidor autentica o cliente no momento do envio, portanto os parâmetros têm a integridade verificada antes de qualquer redirecionamento acontecer.
- Conjuntos longos de parâmetros (pedidos `claims` grandes, fluxos multi-recurso) não estouram os limites de comprimento da URL.

## Endpoint

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

A autenticação é a mesma de `/connect/token`: HTTP Basic com `client_id`/`client_secret`, ou credenciais codificadas em formulário. Clientes confidenciais devem autenticar-se; clientes públicos enviam sem segredo. Falhas de autenticação de cliente retornam `401` (conforme o RFC 9126, ao contrário do endpoint de token, onde apenas `invalid_client` é um 401).

O corpo do formulário carrega os mesmos parâmetros que normalmente iriam em `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, etc.). O próprio `request_uri` é rejeitado: encadear um PAR é proibido pela §2.1 da especificação. Se o corpo carregar um `client_id`, ele deve corresponder ao cliente autenticado.

### Resposta

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

O `request_uri` é de uso único. Ele é removido do store assim que a requisição `/connect/authorize` correspondente o consome (ou quando a janela de 90 segundos expira, o que ocorrer primeiro).

### Passo de autorização

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Quando `request_uri` está presente, todos os outros parâmetros são obtidos do payload enviado: qualquer outra coisa na URL é ignorada. O `client_id` nesta requisição deve corresponder ao cliente que enviou o payload.

## Exigir PAR por cliente

Defina `RequirePushedAuthorizationRequests = true` num cliente para recusar requisições `/connect/authorize` simples vindas dele. Qualquer tentativa de authorize não-PAR retorna `invalid_request` com a descrição "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

Esta é a postura recomendada para clientes que lidam com scopes sensíveis: combinada com PKCE, remove a barra de URL como superfície de ataque.

## Tempo de vida e armazenamento

O tempo de vida do `request_uri` é definido pelo servidor em 90 segundos, correspondendo ao valor típico de um IdP de referência. Os payloads enviados são armazenados via o mesmo `IGrantStore` dos auth codes e refresh tokens, portanto herdam automaticamente a estratégia de persistência e replicação do host.

## Discovery

O endpoint PAR anuncia-se em `.well-known/openid-configuration` como:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
