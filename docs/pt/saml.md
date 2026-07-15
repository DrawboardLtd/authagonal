---
layout: default
title: SAML
locale: pt
---

# SAML 2.0 SP

O Authagonal inclui uma implementação própria de Service Provider SAML 2.0. Sem biblioteca SAML de terceiros: construído sobre `System.Security.Cryptography.Xml.SignedXml` (parte do .NET).

## Âmbito

- **SSO iniciado pelo SP** (o utilizador começa no Authagonal, redirecionado para o IdP)
- **Binding HTTP-Redirect** para AuthnRequest (opcionalmente assinado, ver abaixo)
- **Binding HTTP-POST** para Response (ACS)
- **Asserções encriptadas** (`EncryptedAssertion`) desencriptadas com um par de chaves de SP por conexão
- **Single Logout** (iniciado pelo SP e iniciado pelo IdP, bindings Redirect e POST)
- O Azure AD / Entra ID é o alvo principal, mas qualquer IdP compatível funciona (os nomes de atributos de Okta, OneLogin, Ping, Google Workspace, ADFS e Shibboleth são tratados)

### Não Suportado

- Binding de artefacto
- Encriptação de asserções AES-GCM (limitação do `EncryptedXml` do .NET; configure AES-CBC no IdP, ver abaixo)

O SSO iniciado pelo IdP é suportado. O endpoint ACS trata respostas sem `InResponseTo` (a verificação de request-ID é ignorada para respostas não solicitadas, mas o uso único do ID da asserção continua a ser aplicado, ver Segurança).

## Configuração do Azure AD

### 1. Criar um Provedor SAML

**Opção A: Configuração (recomendado para configurações estáticas)**

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

Os provedores são semeados na inicialização. Os mapeamentos de domínio SSO são registados automaticamente a partir de `AllowedDomains`. Os provedores semeados pela configuração requerem um URL `MetadataLocation` e não recebem um par de chaves de SP (portanto sem AuthnRequests assinados, asserções encriptadas ou mensagens de logout assinadas); use a API de Administração para essas funcionalidades.

`EntityId` é o **ID de entidade do seu SP** (o identificador que regista no IdP), não o ID de entidade do IdP.

**Opção B: API de Administração (para gestão em tempo de execução)**

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

A API gera o `connectionId` (um GUID) e retorna-o no cabeçalho `Location` e no corpo da resposta. Campos opcionais adicionais: `metadataXml` (metadados colados, ver abaixo), `nameIdFormat` (ver abaixo), `signAuthnRequests` (forçar AuthnRequests assinados), `iconUrl` (ícone do botão de login), `disableJitProvisioning` (rejeitar utilizadores desconhecidos em vez de os criar automaticamente). As conexões criadas via API também recebem um par de chaves de SP gerado automaticamente (ver Par de Chaves de SP abaixo).

As conexões são geridas via `POST` / `GET` / `PUT` / `DELETE` em `/api/v1/saml/connections[/{connectionId}]`. `PUT` é uma atualização parcial: apenas os campos fornecidos no pedido são modificados.

### 2. Configurar o Azure AD

1. No Azure AD, vá a Aplicações Empresariais, Nova Aplicação, Criar a sua própria
2. Configure o Single Sign-On com SAML
3. **Identificador (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **URL de Resposta (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **URL de Início de Sessão:** `https://auth.example.com/saml/acme-azure/login`

### 3. Roteamento de Domínio SSO

Quando `AllowedDomains` é especificado (na configuração ou via a API de criação), os mapeamentos de domínio SSO são registados automaticamente. Quando um utilizador introduz `user@acme.com` na página de login, o SPA deteta que o SSO é obrigatório e mostra "Continuar com SSO". Um domínio só pode ser mapeado para uma conexão; a API rejeita um domínio já reivindicado por uma conexão diferente.

Também pode gerir domínios em tempo de execução via a API de Administração; consulte [API de Administração](admin-api).

## Metadados XML Colados

Alguns IdPs não publicam um URL de metadados (Google Workspace), ou o seu endpoint de metadados é inacessível a partir do SP (ADFS em rede privada). Para esses, cole antes o documento de metadados: forneça `metadataXml` na criação/atualização. Exatamente um de `metadataLocation` ou `metadataXml` deve ser fornecido; fornecer um na atualização limpa o outro.

Os metadados colados são validados no momento em que são guardados e **condensados** (`SamlMetadataParser.Condense`) num `EntityDescriptor` mínimo e canónico que contém exatamente o que o SP consome: entityID, certificados de assinatura, o endpoint de SSO, o endpoint de SLO se presente e a flag `WantAuthnRequestsSigned`. Os documentos de fornecedores podem exceder 100KB (o `FederationMetadata.xml` do ADFS), ultrapassando o limite de 64KB de propriedade do Azure Table, enquanto as partes que o SP usa têm alguns KB. Colagens que não podem ser analisadas são rejeitadas com um 400; o documento deve conter um `IDPSSODescriptor` com um certificado de assinatura e um `SingleSignOnService`.

## Formato NameID

O campo `nameIdFormat` controla o `NameIDPolicy` Format solicitado no AuthnRequest:

| Valor | Comportamento |
|---|---|
| omitido / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (o padrão histórico) |
| `"none"` | Omite completamente o elemento `NameIDPolicy`. A definição segura para ADFS: o ADFS falha todo o login (MSIS7070) quando as suas regras de claim não emitem o formato solicitado. |
| qualquer outro valor | Enviado literalmente como o URN de Format (deve começar com `urn:`) |

Na atualização, `""` repõe o padrão emailAddress. Os metadados do SP anunciam o formato solicitado da conexão (e omitem `NameIDFormat` quando definido como `"none"`).

## Endpoints

| Endpoint | Descrição |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Inicia o SSO iniciado pelo SP. Constrói um AuthnRequest (assinado quando aplicável) e redireciona para o IdP. `loginHint` é passado como `login_hint` para os IdPs que o respeitam (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Recebe a Response SAML, valida-a, cria/autentica o utilizador. |
| `GET /saml/{connectionId}/metadata` | XML de metadados do SP para configurar o IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | Single Logout iniciado pelo SP. Termina a sessão local, depois envia um LogoutRequest ao IdP quando este suporta SLO. |
| `GET/POST /saml/{connectionId}/slo` | Endpoint de Single Logout. Recebe LogoutRequests iniciados pelo IdP (binding Redirect ou POST) e o troço de LogoutResponse do SLO iniciado pelo SP. |

O URL de retorno pós-login é transportado do lado do servidor no AuthnRequest armazenado (indexado pelo ID do pedido), não no RelayState: a especificação SAML limita o RelayState a 80 bytes e alguns IdPs truncam-no. O RelayState só é consultado para fluxos iniciados pelo IdP.

## Par de Chaves de SP e Asserções Encriptadas

Cada conexão criada via API recebe um par de chaves de SP gerado automaticamente: um certificado RSA autoassinado de 2048 bits (validade de 10 anos), armazenado como PKCS#12 e protegido em repouso pelo provedor de segredos do host. É apenas do lado do servidor e nunca retornado pela API. O par de chaves permite:

- **AuthnRequests assinados** (assinatura de query `SigAlg`/`Signature` no binding de redirecionamento). A assinatura é ativada automaticamente quando os metadados do IdP declaram `WantAuthnRequestsSigned`, ou sempre quando a conexão define `signAuthnRequests: true`.
- **Desencriptação de asserções encriptadas.** Quando os metadados do SP anunciam um certificado de encriptação, o ADFS começa a encriptar asserções por padrão; o ACS desencripta-as com a chave privada do SP e passa a asserção desencriptada pelo mesmo pipeline de assinatura/condições que uma em texto simples. Suportado: transporte de chave RSA-OAEP (SHA-1/SHA-256) e RSA-1.5; encriptação de dados AES-128/192/256-CBC e 3DES. **AES-GCM não é suportado** (limitação do `EncryptedXml` do .NET) e produz um erro claro; configure o IdP para usar AES-CBC.
- **Mensagens de logout assinadas** (LogoutRequest/LogoutResponse no binding de redirecionamento).

Os metadados do SP publicam o certificado tanto como um `KeyDescriptor` de `signing` como de `encryption`, e definem `AuthnRequestsSigned="true"` quando a conexão força a assinatura.

## Single Logout

O ACS regista a sessão SAML no cookie de autenticação (claims `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`) para que o logout possa ser associado de volta à sessão do IdP.

- **Iniciado pelo SP:** `GET /saml/{connectionId}/logout` termina sempre primeiro a sessão de cookie local (o utilizador pediu para sair; o SLO do IdP é feito da melhor forma possível). Se a sessão do navegador veio desta conexão e os metadados do IdP anunciam um `SingleLogoutService`, um LogoutRequest (NameID + SessionIndex, assinado quando o SP tem uma chave) é enviado via o binding de redirecionamento; o LogoutResponse do IdP volta para `/slo`, que leva o utilizador ao `returnUrl` armazenado. Os IdPs sem endpoint de SLO (Google) recebem apenas o encerramento de sessão local.
- **Iniciado pelo IdP:** o IdP envia um LogoutRequest para `/saml/{connectionId}/slo` (binding Redirect GET ou POST). Os pedidos assinados são validados contra os certificados dos metadados do IdP. **Os LogoutRequests não assinados só são honrados quando a própria sessão do navegador pertence a esta conexão**, portanto um atacante não autenticado não consegue desconectar ninguém a não ser a si próprio. Um LogoutResponse assinado é retornado quando o IdP tem um endpoint de SLO. Apenas front-channel: a mensagem chega no navegador do utilizador, portanto terminar a sessão de cookie desconecta exatamente esse navegador.

## Cache de Metadados e Rotação de Certificados

- Os metadados do IdP obtidos de `MetadataLocation` são armazenados em cache na memória durante 60 minutos (configurável via `Cache:SamlMetadataCacheMinutes`), indexados pelo URL de metadados (não pelo ID da conexão, portanto nenhuma confusão de cache entre tenants é possível).
- Os metadados colados são armazenados em cache endereçados por conteúdo (hash do XML) e nunca são obtidos novamente.
- **Nova obtenção em caso de falha de assinatura:** uma falha de validação de assinatura logo após a rotação de certificado do IdP significa que os metadados em cache estão obsoletos. Nessa falha exata, a entrada de cache é removida e os metadados são obtidos novamente uma vez, depois a validação é repetida, com um período de espera de 5 minutos por localização de metadados para que uma asserção inválida não possa ser usada para martelar o endpoint de metadados do IdP. Sem isto, uma rotação de certificado faria os logins falharem até que o TTL do cache expirasse. (Apenas metadados obtidos por URL; os metadados colados não têm nada para obter novamente.)

## Compatibilidade com Azure AD

| Comportamento do Azure AD | Tratamento |
|---|---|
| Assina apenas a asserção (padrão) | Valida a assinatura no elemento Assertion |
| Assina apenas a resposta | Valida a assinatura no elemento Response |
| Assina ambos | Valida ambas as assinaturas |
| SHA-256 (padrão) | Suporta SHA-256 e SHA-1 |
| NameID: emailAddress | Extração direta do e-mail |
| NameID: persistent (opaco) | Recorre à claim de e-mail dos atributos |
| NameID: unspecified | Recorre à claim de e-mail dos atributos |
| NameID: transient | Roda a cada login, portanto nunca é usado como a chave federada. O atributo estável object-id do IdP é usado em vez disso; se nenhum for afirmado, o login é rejeitado com um erro acionável (configure um NameID persistent ou emailAddress, ou afirme um atributo object-id). |

## Mapeamento de Atributos

Os atributos são indexados de forma insensível a maiúsculas/minúsculas tanto pelo seu `Name` como pelo seu `FriendlyName` (o Okta e o Shibboleth emitem Names OID com FriendlyNames legíveis por humanos; corresponder a qualquer um deles é o que faz o mapeamento de fornecedores funcionar). Cada campo tenta uma lista de aliases por ordem; o primeiro alias é o URI de claim da Microsoft, portanto o comportamento do Entra/ADFS não muda, e os restantes cobrem os nomes friendly e OID que o Okta, OneLogin, Ping, Google e Shibboleth emitem por padrão:

| Campo | Nomes de atributo aceites |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` abrevia o URI completo `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` ou `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Prioridade de resolução do e-mail: atributo de e-mail explícito (qualquer alias) → NameID quando o seu formato é emailAddress → a claim `name` se contiver `@` → rejeitar (um e-mail é obrigatório).

**Os grupos são multivalorados:** cada elemento `AttributeValue` é capturado (um por associação a grupo), não apenas o primeiro.

## Provisionamento JIT

Os utilizadores desconhecidos são criados automaticamente no primeiro login (e-mail, primeiro/último nome da asserção, e-mail marcado como confirmado) e ligados à conexão pela sua identidade federada estável (`saml:{connectionId}` + NameID, ou o object-id para NameIDs transient). Defina `disableJitProvisioning: true` para rejeitar os utilizadores desconhecidos em vez disso. Os utilizadores recorrentes são correspondidos primeiro pela ligação federada, nunca apenas pelo e-mail; uma conta local existente é anexada por e-mail apenas quando os `AllowedDomains` da conexão cobrem o domínio desse e-mail (a declaração explícita do administrador de que este IdP é dono do domínio), prevenindo a tomada de conta via um IdP malicioso.

## Segurança

- **Prevenção de replay:** para fluxos iniciados pelo SP, `InResponseTo` é validado contra um ID de pedido armazenado (de uso único). Independentemente, o ID de cada asserção aceite é armazenado e forçado a uso único, o que também cobre as respostas iniciadas pelo IdP e as respostas cujo `InResponseTo` foi removido (o ID da asserção vive dentro da asserção assinada, portanto não pode ser alterado sem quebrar a assinatura).
- **Desvio de relógio:** tolerância de 5 minutos em NotBefore/NotOnOrAfter
- **Prevenção de ataque de wrapping:** o URI de Reference da assinatura deve corresponder ao ID do elemento assinado
- **Prevenção de redirecionamento aberto:** o URL de retorno pós-login deve ser um caminho relativo à raiz (começando com `/`, sem `//`, sem barras invertidas, uma vez que os navegadores tratam `\` como `/`)
- **Garantia de domínio:** quando `AllowedDomains` está configurado, as asserções para e-mails fora desses domínios são rejeitadas, portanto uma conexão não pode afirmar o domínio de outra nem o e-mail de um utilizador local
- **MFA:** a federação prova apenas o primeiro fator. Se a política efetiva do utilizador exigir MFA, o login é encaminhado pelo desafio/configuração de MFA local em vez de emitir uma sessão totalmente autenticada.
