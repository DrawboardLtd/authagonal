---
layout: default
title: SAML
locale: pt
---

# SAML 2.0 SP

O Authagonal inclui uma implementação própria de Service Provider SAML 2.0. Sem biblioteca SAML de terceiros — construído sobre `System.Security.Cryptography.Xml.SignedXml` (parte do .NET).

## Âmbito

- **SSO iniciado pelo SP** (utilizador começa no Authagonal, redirecionado para o IdP)
- **Binding HTTP-Redirect** para AuthnRequest
- **Binding HTTP-POST** para Response (ACS)
- O Azure AD é o alvo principal, mas qualquer IdP compatível funciona

### Não Suportado

- SSO iniciado pelo IdP
- Logout SAML (use timeout de sessão)
- Encriptação de asserções (não publique um certificado de encriptação)
- Binding de artefacto

## Configuração do Azure AD

### 1. Criar um Provedor SAML

**Opção A — Configuração (recomendado para configurações estáticas):**

Adicione ao `appsettings.json`:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Os provedores são semeados na inicialização. Os mapeamentos de domínio SSO são registados automaticamente a partir de `AllowedDomains`.

**Opção B — API de Administração (para gestão em tempo de execução):**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

### 2. Configurar o Azure AD

1. No Azure AD, vá a Aplicações Empresariais, Nova Aplicação, Criar a sua própria
2. Configure o Single Sign-On com SAML
3. **Identificador (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **URL de Resposta (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **URL de Início de Sessão:** `https://auth.example.com/saml/acme-azure/login`

### 3. Roteamento de Domínio SSO

Quando `AllowedDomains` é especificado (na configuração ou via a API de criação), os mapeamentos de domínio SSO são registados automaticamente. Quando um utilizador introduz `user@acme.com` na página de login, o SPA deteta que o SSO é obrigatório e mostra "Continuar com SSO".

Também pode gerir domínios em tempo de execução via a API de Administração — consulte [API de Administração](admin-api).

## Endpoints

| Endpoint | Descrição |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...` | Inicia o SSO iniciado pelo SP. Constrói um AuthnRequest e redireciona para o IdP. |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Recebe a Response SAML, valida-a, cria/autentica o utilizador. |
| `GET /saml/{connectionId}/metadata` | XML de metadados do SP para configurar o IdP. |

## Compatibilidade com Azure AD

| Comportamento do Azure AD | Tratamento |
|---|---|
| Assina apenas a asserção (padrão) | Valida a assinatura no elemento Assertion |
| Assina apenas a resposta | Valida a assinatura no elemento Response |
| Assina ambos | Valida ambas as assinaturas |
| SHA-256 (padrão) | Suporta SHA-256 e SHA-1 |
| NameID: emailAddress | Extração direta do e-mail |
| NameID: persistent (opaco) | Recorre à claim de e-mail dos atributos |
| NameID: transient, unspecified | Recorre à claim de e-mail dos atributos |

## Mapeamento de Claims

As claims do Azure AD (formato URI completo) são mapeadas para nomes simples:

| URI da Claim Azure AD | Mapeado Para |
|---|---|
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `email` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname` | `firstName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname` | `lastName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` | `name` (UPN) |
| `http://schemas.microsoft.com/identity/claims/objectidentifier` | `oid` |
| `http://schemas.microsoft.com/identity/claims/tenantid` | `tenantId` |
| `http://schemas.microsoft.com/identity/claims/displayname` | `displayName` |

## Segurança

- **Prevenção de replay:** InResponseTo é validado contra um ID de pedido armazenado. Cada ID é de uso único.
- **Desvio de relógio:** Tolerância de 5 minutos em NotBefore/NotOnOrAfter
- **Prevenção de ataque de wrapping:** A validação de assinatura usa a resolução de referência correta
- **Prevenção de redirecionamento aberto:** O RelayState (returnUrl) deve ser um caminho relativo à raiz (começando com `/`, sem esquema ou host)
