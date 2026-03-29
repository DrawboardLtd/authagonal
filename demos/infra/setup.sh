#!/usr/bin/env bash
set -euo pipefail

# ---------------------------------------------------------------------------
# Authagonal Demo — Azure infrastructure setup
# Run once to create all resources. Idempotent (safe to re-run).
# ---------------------------------------------------------------------------

usage() {
  echo "Usage: $0 --subscription <id> [--location <region>]"
  echo "  --subscription, -s   Azure subscription ID (required)"
  echo "  --location, -l       Azure region (default: australiaeast)"
  exit 1
}

SUBSCRIPTION=""
LOCATION="australiaeast"

while [[ $# -gt 0 ]]; do
  case $1 in
    -s|--subscription) SUBSCRIPTION="$2"; shift 2 ;;
    -l|--location)     LOCATION="$2"; shift 2 ;;
    *) usage ;;
  esac
done

[[ -z "$SUBSCRIPTION" ]] && usage

RG="authagonal-demo"
STORAGE_ACCOUNT="authagonaldemo"
ACA_ENV="authagonal-demo-env"

echo "==> Setting subscription"
az account set --subscription "$SUBSCRIPTION"

echo "==> Creating resource group"
az group create --name "$RG" --location "$LOCATION" --output none

echo "==> Creating storage account"
az storage account create \
  --name "$STORAGE_ACCOUNT" \
  --resource-group "$RG" \
  --location "$LOCATION" \
  --sku Standard_LRS \
  --kind StorageV2 \
  --min-tls-version TLS1_2 \
  --output none

STORAGE_CONN=$(az storage account show-connection-string \
  --name "$STORAGE_ACCOUNT" \
  --resource-group "$RG" \
  --query connectionString -o tsv | tr -d '\r')

echo "==> Creating Container Apps environment (no Log Analytics)"
az containerapp env create \
  --name "$ACA_ENV" \
  --resource-group "$RG" \
  --location "$LOCATION" \
  --logs-destination none \
  --output none

ACA_ENV_ID=$(az containerapp env show \
  --name "$ACA_ENV" \
  --resource-group "$RG" \
  --query id -o tsv | tr -d '\r')

ENV_DOMAIN=$(az containerapp env show \
  --name "$ACA_ENV" \
  --resource-group "$RG" \
  --query properties.defaultDomain -o tsv | tr -d '\r')

AUTH_URL="https://authagonal-auth.${ENV_DOMAIN}"
FRONTEND_URL="https://authagonal-frontend.${ENV_DOMAIN}"

echo "==> Auth server URL will be:   $AUTH_URL"
echo "==> Frontend URL will be:      $FRONTEND_URL"

# ---------------------------------------------------------------------------
# Container Apps
# ---------------------------------------------------------------------------

echo "==> Creating auth server container app"
az containerapp create \
  --name authagonal-auth \
  --resource-group "$RG" \
  --environment "$ACA_ENV" \
  --image mcr.microsoft.com/k8se/quickstart:latest \
  --target-port 8080 \
  --ingress external \
  --min-replicas 1 \
  --max-replicas 1 \
  --cpu 0.5 \
  --memory 1Gi \
  --output none

echo "==> Configuring auth server env vars"
az containerapp update \
  --name authagonal-auth \
  --resource-group "$RG" \
  --set-env-vars \
    "Storage__ConnectionString=${STORAGE_CONN}" \
    "Issuer=${AUTH_URL}" \
    "Oidc__Issuer=${AUTH_URL}" \
    "AdminApi__Enabled=false" \
  --output none

echo "==> Configuring auth server client seed"
az containerapp update \
  --name authagonal-auth \
  --resource-group "$RG" \
  --set-env-vars \
    "Clients__0__Id=sample-app" \
    "Clients__0__Name=Sample Application" \
    "Clients__0__GrantTypes__0=authorization_code" \
    "Clients__0__GrantTypes__1=refresh_token" \
    "Clients__0__RedirectUris__0=${FRONTEND_URL}/callback" \
    "Clients__0__PostLogoutRedirectUris__0=${FRONTEND_URL}" \
    "Clients__0__Scopes__0=openid" \
    "Clients__0__Scopes__1=profile" \
    "Clients__0__Scopes__2=email" \
    "Clients__0__Scopes__3=offline_access" \
    "Clients__0__CorsOrigins__0=${FRONTEND_URL}" \
    "Clients__0__RequirePkce=true" \
    "Clients__0__RequireSecret=false" \
    "Clients__0__AllowOfflineAccess=true" \
  --output none

echo "==> Configuring Google OIDC provider seed"
echo "    NOTE: Set GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET env vars before running,"
echo "    or update the container app env vars afterwards."
az containerapp update \
  --name authagonal-auth \
  --resource-group "$RG" \
  --set-env-vars \
    "OidcProviders__0__ConnectionId=google" \
    "OidcProviders__0__ConnectionName=Google" \
    "OidcProviders__0__MetadataLocation=https://accounts.google.com/.well-known/openid-configuration" \
    "OidcProviders__0__ClientId=${GOOGLE_CLIENT_ID:-REPLACE_ME}" \
    "OidcProviders__0__ClientSecret=${GOOGLE_CLIENT_SECRET:-REPLACE_ME}" \
    "OidcProviders__0__RedirectUrl=${AUTH_URL}/oidc/callback" \
  --output none

echo "==> Creating sample API container app (internal ingress)"
az containerapp create \
  --name authagonal-api \
  --resource-group "$RG" \
  --environment "$ACA_ENV" \
  --image mcr.microsoft.com/k8se/quickstart:latest \
  --target-port 8080 \
  --ingress internal \
  --min-replicas 1 \
  --max-replicas 1 \
  --cpu 0.25 \
  --memory 0.5Gi \
  --output none

echo "==> Configuring sample API env vars"
az containerapp update \
  --name authagonal-api \
  --resource-group "$RG" \
  --set-env-vars \
    "Auth__Authority=${AUTH_URL}" \
    "Auth__Audience=sample-app" \
    "Auth__AllowHttp=false" \
    "Cors__Origins__0=${FRONTEND_URL}" \
  --output none

echo "==> Creating frontend container app"
az containerapp create \
  --name authagonal-frontend \
  --resource-group "$RG" \
  --environment "$ACA_ENV" \
  --image mcr.microsoft.com/k8se/quickstart:latest \
  --target-port 8080 \
  --ingress external \
  --min-replicas 1 \
  --max-replicas 1 \
  --cpu 0.25 \
  --memory 0.5Gi \
  --output none

echo "==> Configuring frontend env vars"
az containerapp update \
  --name authagonal-frontend \
  --resource-group "$RG" \
  --set-env-vars \
    "AUTH_SERVER=${AUTH_URL}" \
    "REDIRECT_URI=${FRONTEND_URL}/callback" \
    "API_BASE=http://authagonal-api.internal.${ENV_DOMAIN}" \
  --output none

# ---------------------------------------------------------------------------
# Service principal for GitHub Actions
# ---------------------------------------------------------------------------

echo "==> Creating app registration for GitHub Actions OIDC"
APP_ID=$(az ad app create --display-name "github-authagonal-demo" --query appId -o tsv | tr -d '\r')
SP_ID=$(az ad sp create --id "$APP_ID" --query id -o tsv | tr -d '\r')

echo "==> Assigning Contributor role on resource group"
az role assignment create \
  --assignee "$SP_ID" \
  --role Contributor \
  --scope "/subscriptions/${SUBSCRIPTION}/resourceGroups/${RG}" \
  --output none

echo "==> Adding federated credential for GitHub Actions (azure-demo environment)"
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{
    \"name\": \"github-azure-demo\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:DrawboardLtd/authagonal:environment:azure-demo\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }" \
  --output none

echo "    NOTE: Create a GitHub environment named 'azure-demo' in repo settings."

TENANT_ID=$(az account show --query tenantId -o tsv | tr -d '\r')

echo ""
echo "==> Done! Container Apps deployed."
echo "    Auth server:  $AUTH_URL"
echo "    Frontend:     $FRONTEND_URL"
echo ""
echo "    Add these GitHub repo secrets:"
echo "      AZURE_CLIENT_ID:       $APP_ID"
echo "      AZURE_TENANT_ID:       $TENANT_ID"
echo "      AZURE_SUBSCRIPTION_ID: $SUBSCRIPTION"
