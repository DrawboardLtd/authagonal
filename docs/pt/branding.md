---
layout: default
title: Personalização Visual
locale: pt
---

# Personalização Visual da Interface de Login

O SPA de login é configurável em tempo de execução via um ficheiro `branding.json` servido a partir da raiz web. Não é necessário rebuild, basta montar a sua configuração e recursos.

## Como Funciona

Na inicialização, o SPA obtém `/branding.json`. Se o ficheiro não existir ou estiver inacessível, os valores padrão são utilizados. (Um servidor host também pode incorporar a configuração como um payload de arranque `<script type="application/json" id="authagonal-boot">`; quando presente, o SPA lê-o em vez de o obter.) A configuração controla:

- Nome da aplicação (exibido no cabeçalho e no título da página)
- Imagem do logotipo, com um "chip" de fundo opcional por modo
- Cor primária (botões, links, anéis de foco), com uma variante opcional para modo escuro
- Cores de fundo da página e do cartão, por modo
- Visibilidade dos links de esquecimento de senha e de registo
- Padrão do modo escuro (claro / seguir o SO / escuro)
- Opções do seletor de idioma
- O rodapé "Powered by Authagonal"
- CSS personalizado para estilização mais profunda

## Configuração

Coloque um ficheiro `branding.json` no diretório `wwwroot/` (ou monte-o no container Docker):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "darkPrimaryColor": "#3b82f6",
  "darkMode": "auto",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### Opções

| Propriedade | Tipo | Padrão | Descrição |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Exibido no cabeçalho e no título da aba do navegador |
| `logoUrl` | `string \| null` | `null` | URL para uma imagem de logotipo. Quando definido, substitui o cabeçalho de texto. |
| `primaryColor` | `string` | `"#2563eb"` | Cor hexadecimal para botões, links e indicadores de foco |
| `supportEmail` | `string \| null` | `null` | E-mail de contacto para suporte (reservado para uso futuro) |
| `showForgotPassword` | `boolean` | `true` | Mostrar/ocultar o link "Esqueceu a senha?" na página de login |
| `showRegistration` | `boolean` | `false` | Mostrar/ocultar o link de registo de autoatendimento |
| `customCssUrl` | `string \| null` | `null` | URL para um ficheiro CSS personalizado carregado após os estilos padrão |
| `welcomeTitle` | `LocalizedString` | `null` | Substituir o título da página de login (string simples ou `{ "en": "...", "de": "..." }`) |
| `welcomeSubtitle` | `LocalizedString` | `null` | Substituir o subtítulo da página de login |
| `languages` | `array \| null` | `null` | Opções do seletor de idioma (`[{ "code": "en", "label": "English" }, ...]`). `null` mostra todos os idiomas incluídos exceto locales de novidade (consulte [Localização](localization)). |
| `poweredBy` | `boolean` | `true` | Mostrar/ocultar o rodapé "Powered by Authagonal" nas páginas de autenticação |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Tema padrão quando o visitante ainda não escolheu um: `"off"` (apenas claro), `"auto"` (seguir a preferência do SO), `"force"` (sempre escuro). A alternância de tema do visitante continua a prevalecer. |
| `lightBg` | `string \| null` | `null` | Cor de fundo da página no modo claro |
| `lightCardBg` | `string \| null` | `null` | Cor de fundo do cartão/formulário no modo claro |
| `darkBg` | `string \| null` | `null` | Cor de fundo da página no modo escuro |
| `darkCardBg` | `string \| null` | `null` | Cor de fundo do cartão/formulário no modo escuro |
| `darkPrimaryColor` | `string \| null` | `null` | Substitui `primaryColor` no modo escuro |
| `lightLogoBg` | `string \| null` | `null` | Fundo do chip do logotipo no modo claro (ver abaixo) |
| `darkLogoBg` | `string \| null` | `null` | Fundo do chip do logotipo no modo escuro (ver abaixo) |

Os valores de cor devem ser uma cor hexadecimal (`#rgb`, `#rrggbb`, `#rrggbbaa`) ou uma expressão `rgb()`/`rgba()`/`hsl()`/`hsla()`; qualquer outra coisa é ignorada. As cores por modo são injetadas como uma regra `<style id="branding-theme-vars">` após os estilos incluídos (valores claros em `:root`, valores escuros em `.dark`), portanto um valor escuro pode diferir do seu equivalente claro.

### Chip de Fundo do Logotipo

Se o seu logotipo tiver arte branca ou transparente, ele pode desaparecer sobre o cartão claro. Defina `lightLogoBg` e/ou `darkLogoBg` para renderizar o logotipo dentro de um "chip" arredondado e com espaçamento, com essa cor de fundo:

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

O chip (um invólucro `data-auth="logo-chip"` controlado pela variável CSS `--auth-logo-bg`) só recebe o seu espaçamento e fundo quando um fundo de logotipo é configurado, portanto os tenants que não definem um veem o logotipo encostado ao cartão exatamente como antes. Os dois campos são independentes: defina apenas `lightLogoBg` para aplicar o chip ao logotipo no modo claro e deixá-lo nu no modo escuro.

## Exemplo Docker

Monte os seus ficheiros de personalização no container:

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

Ou com docker-compose:

```yaml
services:
  authagonal:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./my-branding/branding.json:/app/wwwroot/branding.json
      - ./my-branding/assets:/app/wwwroot/branding
    environment:
      - Storage__ConnectionString=...
      - Issuer=https://auth.example.com
```

## CSS Personalizado

A opção `customCssUrl` carrega uma folha de estilos adicional após os estilos padrão, para que as suas regras tenham precedência. Útil para alterar fontes, ajustar espaçamentos ou re-estilizar elementos específicos. O URL deve ser da mesma origem (URLs relativos como `/branding/custom.css` são aceites); folhas de estilo de origem cruzada são silenciosamente ignoradas.

### Propriedades Personalizadas CSS

A interface de login expõe várias propriedades personalizadas CSS para um controlo detalhado:

| Propriedade | Padrão | Descrição |
|---|---|---|
| `--brand-primary` | `#2563eb` | Cor primária para botões, links, anéis de foco |
| `--auth-bg` | `#f3f4f6` | Cor de fundo da página |
| `--auth-card-bg` | `#ffffff` | Cor de fundo do cartão/formulário |
| `--auth-logo-bg` | `transparent` | Fundo do chip do logotipo (o espaçamento do chip só aparece quando um fundo de logotipo é configurado) |
| `--auth-radius` | `0.5rem` | Raio da borda do cartão de autenticação |
| `--auth-font` | *(herdado; pilha de fontes do sistema)* | Família de fontes do cartão de autenticação |
| `--auth-heading` | `#111827` | Cor do texto dos títulos |

As variáveis de cor aqui mapeiam diretamente para os campos de configuração (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`), portanto prefira a configuração para alterações de cor simples e reserve o CSS personalizado para todo o resto.

Substitua-as no seu CSS personalizado:

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

A interface de login usa Tailwind CSS. O CSS personalizado pode visar elementos HTML padrão e classes utilitárias do Tailwind. Os componentes de interface exportados (`Button`, `Input`, `Card`, `Alert`, etc.) usam Tailwind internamente.

## Modo Escuro

O SPA de login vem com temas claro, escuro e **de sistema**. A alternância de tema está sempre visível no layout. A seleção do utilizador é persistida no `localStorage` sob a chave `auth-theme`.

### Como Funciona

- **Padrão**: até o visitante escolher um tema, a opção de personalização `darkMode` define o padrão: `"off"` (claro), `"auto"` (sistema, o padrão) ou `"force"` (escuro). Assim que o visitante usa a alternância, a sua escolha prevalece sempre.
- **Deteção**: quando o tema é "sistema", o SPA observa `window.matchMedia('(prefers-color-scheme: dark)')` e reaplica o tema automaticamente à medida que a preferência do SO muda.
- **Aplicação**: o SPA alterna uma classe `.dark` no `<html>`. A variante escura do Tailwind (`&:where(.dark, .dark *)`) ativa os estilos escuros compilados em cada componente.
- **Persistência**: as escolhas explícitas "claro" / "escuro" / "sistema" são armazenadas no `localStorage`.

### Variáveis CSS

Os valores claros são declarados em `:root`; as substituições do modo escuro têm escopo em `.dark`, portanto a personalização do tenant em `customCssUrl` tem sempre precedência quando fornecida.

| Variável | Claro | Escuro |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (ou `lightBg`) | `#030712` (ou `darkBg`) |
| `--auth-card-bg` | `#ffffff` (ou `lightCardBg`) | `#111827` (ou `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (ou `lightLogoBg`) | `transparent` (ou `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (ou `primaryColor`) | o valor claro (ou `darkPrimaryColor`) |

### Desativar ou Substituir

A personalização do tenant prevalece sempre. Para forçar um único tema, defina os seus próprios valores em `customCssUrl`:

```css
/* Force dark palette regardless of user choice */
:root {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
.dark {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Para remover a alternância de tema por completo, use o caminho do pacote npm: importe o `AuthLayout` e renderize sem a alternância, ou faça fork do SPA.

### Atributos de Dados

Todos os elementos do formulário de login têm atributos `data-auth` para segmentação por CSS e automação de testes:

| Atributo | Elemento |
|---|---|
| `data-auth="page"` | Invólucro principal da página |
| `data-auth="header"` | Seção do cabeçalho |
| `data-auth="logo-chip"` | Invólucro em torno da imagem do logotipo (com espaçamento apenas quando um fundo de logotipo é definido) |
| `data-auth="logo"` | Imagem do logotipo |
| `data-auth="app-name"` | Título do nome da aplicação |
| `data-auth="content"` | Área de conteúdo principal |
| `data-auth="languages"` | Seletor de idioma |
| `data-auth="language-trigger"` | Botão de acionamento do seletor de idioma |
| `data-auth="theme-toggle"` | Alternância de tema claro/sistema/escuro |
| `data-auth="powered-by"` | Rodapé "Powered by Authagonal" |

Segmente-os no seu CSS personalizado:

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Exemplo: Fundo e Fonte Personalizados

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Níveis de Personalização

| Nível | O que Faz | Caminho de Atualização |
|---|---|---|
| **Apenas configuração** | Montar `branding.json` + logotipo | Transparente: atualize a imagem Docker, mantenha os seus mounts |
| **Configuração + CSS** | Adicionar `customCssUrl` com substituições de estilo | Igual: as classes CSS são estáveis |
| **Pacote npm** | `npm install @authagonal/login`, personalizar `branding.json`, compilar no `wwwroot/` | Atualizável: `npm update` puxa novas versões |
| **Fork do SPA** | Clonar `login-app/`, modificar o código-fonte, compilar o seu próprio | A interface é sua: as atualizações do servidor são independentes |
| **Escrever o seu próprio** | Construir um frontend completamente personalizado contra a API de autenticação | Controlo total: consulte [API de Autenticação](auth-api) para o contrato |

Consulte `demos/custom-server/` para um exemplo funcional com personalização visual (tema verde, "Acme Corp").
