---
layout: default
title: Logout Front-Channel
locale: pt
---

# Logout Front-Channel

O Authagonal implementa o **OpenID Connect Front-Channel Logout 1.0**, um mecanismo de logout conduzido pelo navegador que complementa o [logout back-channel](index#features). Enquanto o logout back-channel é um POST de servidor para servidor, o logout front-channel renderiza a URL de logout de cada parte confiante (RP) num iframe oculto, de modo que a sessão de navegador de cada aplicação (cookies, local storage) seja limpa a partir de dentro do navegador do utilizador.

## Quando Usar Cada Um

| Aspeto | Back-Channel | Front-Channel |
|---|---|---|
| Sessões do lado do servidor | ✅ | ❌ |
| Cookies do navegador / local storage | ❌ | ✅ |
| Funciona quando o navegador do utilizador está offline | ✅ | ❌ |
| Sobrevive a erros de rede (repetição) | ✅ | ❌ (uma única tentativa de melhor esforço) |

A maioria das aplicações beneficia de configurar **ambos**. O back-channel garante que o servidor é avisado; o front-channel limpa o navegador.

## Configuração do Cliente

Adicione uma URI de logout front-channel ao registo `OAuthClient`:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Campo | Descrição |
|---|---|
| `FrontChannelLogoutUri` | O endpoint de logout do cliente visível ao navegador |
| `FrontChannelLogoutSessionRequired` | Se `true` (padrão), a URL é chamada com os parâmetros de query `iss` e `sid` para que o cliente possa correlacionar o logout com a sessão específica |

## Como Funciona

Quando o navegador visita `/connect/endsession`:

1. O servidor encontra todos os clientes com os quais o utilizador tem concessões atualmente.
2. Para cada cliente com uma `FrontChannelLogoutUri`, o servidor constrói uma URL, anexando `iss=<issuer>` (e `sid=<session_id>`, quando a sessão tem um) se `FrontChannelLogoutSessionRequired` for `true`.
3. O servidor termina a sessão do utilizador no cookie do servidor de autorização, dispara notificações de logout back-channel em segundo plano e retorna uma página HTML contendo um `<iframe>` oculto para cada URL de logout de cliente:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
4. Após um período de tolerância de 2 segundos, o navegador é redirecionado para `post_logout_redirect_uri`, honrado apenas quando a requisição também carrega um `id_token_hint` que identifica o cliente e a URI está nos `PostLogoutRedirectUris` registados desse cliente (um parâmetro `state`, se fornecido, é anexado ao redirecionamento). Caso contrário, é exibida uma confirmação de "sessão terminada".

## Handler de Logout do Lado do Cliente

Cada parte confiante (RP) deve implementar a URL referenciada por `FrontChannelLogoutUri`. Um handler mínimo:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Verifique que `iss` corresponde ao servidor de autorização esperado.
2. Se `sid` for fornecido, confirme que corresponde ao ID de sessão do cookie de sessão.
3. Limpe a sessão local (cookies, sessão do lado do servidor, armazenamento da SPA).
4. Responda com `200 OK` e um corpo vazio (ou uma página minúscula): a resposta nunca é visível para o utilizador.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Documento de Descoberta

O logout front-channel é anunciado em `/.well-known/openid-configuration`:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Registo Dinâmico de Clientes

Os clientes registados via [Registo Dinâmico de Clientes](client-registration) podem incluir:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

## Limitações

- **Melhor esforço**: os iframes são carregados uma vez. Se um erro de rede ou uma extensão do navegador os bloquear, não há repetição. Combine com o logout back-channel para maior fiabilidade.
- **Cookies de terceiros**: alguns navegadores bloqueiam cookies em iframes cross-site por padrão. Se a sua RP depende de cookies first-party, confirme que o handler de logout não depende do envio de cookies.
- **Timeout**: a página espera ~2 segundos antes de redirecionar/confirmar. Handlers de logout de RP pesados podem não completar a tempo.

## Relacionados

- [Registo Dinâmico de Clientes](client-registration): parâmetros front-channel na requisição de registo
- [Scopes OAuth](scopes): o consentimento ciente de scopes complementa o fluxo de logout
