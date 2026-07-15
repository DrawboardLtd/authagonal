---
layout: default
title: Localização
locale: pt
---

# Localização

A interface de login inclui onze idiomas prontos a usar: Inglês, Chinês Simplificado (`zh-Hans`), Alemão (`de`), Francês (`fr`), Espanhol (`es`), Vietnamita (`vi`), Português (`pt`), Árabe (`ar`), Afrikaans (`af`), Hindi (`hi`) e um idioma de novidade Klingon (`tlh`). As respostas da API do servidor são localizadas nos primeiros sete destes. A localização abrange as respostas da API do servidor, a interface de login e este site de documentação.

## Idiomas Suportados

| Código | Idioma | Interface de Login | API do Servidor |
|---|---|---|---|
| `en` | Inglês (padrão) | ✓ | ✓ |
| `zh-Hans` | Chinês Simplificado | ✓ | ✓ |
| `de` | Alemão | ✓ | ✓ |
| `fr` | Francês | ✓ | ✓ |
| `es` | Espanhol | ✓ | ✓ |
| `vi` | Vietnamita | ✓ | ✓ |
| `pt` | Português | ✓ | ✓ |
| `ar` | Árabe (da direita para a esquerda) | ✓ | — |
| `af` | Afrikaans | ✓ | — |
| `hi` | Hindi | ✓ | — |
| `tlh` | Klingon (novidade) | ✓ | — |

## Servidor (Respostas da API)

O servidor usa a localização integrada do ASP.NET Core com `IStringLocalizer<T>` e ficheiros de recursos `.resx`. O idioma é selecionado a partir do cabeçalho HTTP `Accept-Language`.

### O que é localizado

- Mensagens de erro de validação de senha
- Rótulos da política de senhas (`GET /api/auth/password-policy`)
- Mensagens do fluxo de redefinição de senha (erros de token, expiração, sucesso)
- Descrições genéricas de erros do middleware de tratamento de exceções
- Mensagens de gestão de utilizadores administrativos (confirmação de e-mail, verificação, etc.)
- Mensagem de confirmação de encerramento de sessão

### O que NÃO é localizado

- Códigos de `error` legíveis por máquina (`"email_required"`, `"invalid_credentials"`, etc.): estes são contratos da API e permanecem constantes
- Códigos de erro OAuth/OIDC e descrições de erros direcionadas a desenvolvedores nos endpoints de token, autorização e revogação
- Mensagens internas de log e mensagens de exceção

### Testar a localização do servidor

Envie um cabeçalho `Accept-Language` para qualquer endpoint localizado:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Ficheiros de recursos

Todas as strings de tradução do servidor estão em ficheiros `.resx` em `src/Authagonal.Server/Resources/`:

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
  SharedMessages.vi.resx
  SharedMessages.pt.resx
```

## Interface de Login

O SPA de login usa [react-i18next](https://react.i18next.com/) para localização do lado do cliente. O idioma é detetado automaticamente a partir da definição `navigator.language` do navegador.

Os idiomas registados vivem num único registo `LANGUAGES` em `login-app/src/i18n/index.ts`, que controla tanto o registo de recursos do i18next como todos os seletores de idioma, para que os dois não possam divergir. Os idiomas marcados como `novelty` (atualmente `tlh`) permanecem totalmente funcionais (`?lng=tlh` funciona), mas são excluídos do seletor predefinido; só aparecem numa lista pendente quando o `BrandingConfig.languages` de um inquilino os lista explicitamente. Os inquilinos também podem restringir o seletor da mesma forma: um array `languages` em `branding.json` substitui a lista predefinida por completo (ver [Personalização Visual](branding)).

O idioma ativo é refletido em `<html lang>` e `<html dir>`, pelo que os idiomas da direita para a esquerda (`ar`) invertem o cartão de autenticação automaticamente, incluindo quando o idioma é alterado no momento através do seletor.

### Deteção de idioma

A ordem de deteção é:

1. **localStorage**: preferência persistida de uma visita anterior
2. **Parâmetro de query**: `?lng=de` sobrepõe a deteção do navegador
3. **Idioma do navegador**: `navigator.language` (automático)
4. **Fallback**: Inglês (`en`)

### Ficheiros de tradução

Os ficheiros JSON de tradução são empacotados com a aplicação em `login-app/src/i18n/`:

```
i18n/
  index.ts        # i18n initialization + the LANGUAGES registry
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
  vi.json         # Vietnamese
  pt.json         # Portuguese
  ar.json         # Arabic
  af.json         # Afrikaans
  hi.json         # Hindi
  tlh.json        # Klingon (novelty)
```

### Rótulos da política de senhas

A página de redefinição de senha traduz a sua lista de verificação de requisitos de senha do lado do cliente com base na chave `rule` retornada por `GET /api/auth/password-policy` (recorrendo ao `label` fornecido pelo servidor para regras não reconhecidas). Isto garante que os requisitos seguem o idioma selecionado na interface, mesmo que o cabeçalho `Accept-Language` do navegador seja diferente. A página de registo exibe os valores `label` fornecidos pelo servidor, que são localizados a partir de `Accept-Language`.

### Consumidores do pacote npm

Se consumir a aplicação de login via `@authagonal/login`, a instância i18n é exportada:

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Documentação

O site de documentação usa uma abordagem baseada em diretórios. As páginas em inglês estão na raiz, e as traduções estão em subdiretórios de idioma (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`, `/vi/`, `/pt/`). Um seletor de idioma no menu lateral permite alternar entre idiomas.

## Adicionar um Novo Idioma

Para adicionar suporte a um novo idioma (ex.: Japonês `ja`):

### 1. Servidor

Crie um novo ficheiro `.resx` copiando o inglês e traduzindo os valores:

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

Adicione `"ja"` ao array de culturas suportadas em `AuthagonalExtensions.cs`:

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja" };
```

### 2. Interface de Login

Crie um novo ficheiro JSON de tradução copiando `en.json` e traduzindo os valores:

```
login-app/src/i18n/ja.json
```

Registe-o no array `LANGUAGES` em `login-app/src/i18n/index.ts`. Essa única entrada regista o recurso do i18next e adiciona o idioma a todos os seletores:

```typescript
import ja from './ja.json';

// In the LANGUAGES array:
{ code: 'ja', label: '日本語', resource: ja },
```

### 3. Documentação

Crie um novo diretório com ficheiros markdown traduzidos:

```
docs/ja/
  index.md
  installation.md
  quickstart.md
  ...
```

Adicione um padrão de idioma em `docs/_config.yml`:

```yaml
defaults:
  - scope:
      path: "ja"
    values:
      locale: "ja"
```

Adicione a opção de idioma ao seletor em `docs/_layouts/default.html`.

## Adicionar Novas Strings

### Servidor

1. Adicione a chave e o valor em inglês a `SharedMessages.resx`
2. Adicione os valores traduzidos ao ficheiro `.resx` de cada idioma
3. Use `IStringLocalizer<SharedMessages>` para aceder à string:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Interface de Login

1. Adicione a chave e o valor em inglês a `en.json`
2. Adicione os valores traduzidos ao ficheiro JSON de cada idioma
3. Use a função `t()` nos componentes:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
