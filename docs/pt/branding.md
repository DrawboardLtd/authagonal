---
layout: default
title: Personalização Visual
locale: pt
---

# Personalização Visual da Interface de Login

O SPA de login é configurável em tempo de execução via um ficheiro `branding.json` servido a partir da raiz web. Não é necessário rebuild — basta montar a sua configuração e recursos.

## Como Funciona

Na inicialização, o SPA obtém `/branding.json`. Se o ficheiro não existir ou estiver inacessível, os valores padrão são utilizados. A configuração controla:

- Nome da aplicação (exibido no cabeçalho e título da página)
- Imagem do logotipo
- Cor primária (botões, links, anéis de foco)
- Visibilidade do link de esquecimento de senha
- CSS personalizado para estilização mais profunda

## Configuração

Coloque um ficheiro `branding.json` no diretório `wwwroot/` (ou monte-o no container Docker):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
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
| `languages` | `array \| null` | `null` | Opções do seletor de idioma (`[{ "code": "en", "label": "English" }, ...]`) |

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

A opção `customCssUrl` carrega uma folha de estilos adicional após os estilos padrão, para que as suas regras tenham precedência. Útil para alterar fontes, ajustar espaçamentos ou re-estilizar elementos específicos.

### Propriedades Personalizadas CSS

A cor primária é definida via a propriedade personalizada CSS `--brand-primary` (que alimenta o tema Tailwind). Substitua-a no seu CSS personalizado em vez de usar o `branding.json`:

```css
:root {
  --brand-primary: #059669;
}
```

A interface de login usa Tailwind CSS. O CSS personalizado pode visar elementos HTML padrão e classes utilitárias do Tailwind. Os componentes de interface exportados (`Button`, `Input`, `Card`, `Alert`, etc.) usam Tailwind internamente.

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
| **Apenas configuração** | Montar `branding.json` + logotipo | Transparente — atualize a imagem Docker, mantenha os seus mounts |
| **Configuração + CSS** | Adicionar `customCssUrl` com substituições de estilo | Igual — as classes CSS são estáveis |
| **Pacote npm** | `npm install @authagonal/login`, personalizar `branding.json`, compilar no `wwwroot/` | Atualizável — `npm update` puxa novas versões |
| **Fork do SPA** | Clonar `login-app/`, modificar o código-fonte, compilar o seu próprio | Você possui a interface — atualizações do servidor são independentes |
| **Escrever o seu próprio** | Construir um frontend completamente personalizado contra a API de autenticação | Controlo total — consulte [API de Autenticação](auth-api) para o contrato |

Consulte `demos/custom-server/` para um exemplo funcional com personalização visual (tema verde, "Acme Corp").
