#!/usr/bin/env bash
# Creates or updates Joevar's Azure resources (infra/main.bicep) and connects the two GitHub repos' deploy workflows.
# Safe to re-run. Needs az (signed in with `az login`), dotnet and python3; gh (signed in with `gh auth login`) to set
# the GitHub secrets and variables, otherwise it prints what to set by hand.
#
#   infra/deploy.sh [resource-group]        default: joevar-rg
set -euo pipefail
cd "$(dirname "$0")/.."

group=${1:-joevar-rg}
location=centralus
api_name=joevar-api
web_name=joevar-web
database_server=joevar-pg
api_repo=Knucklehead88/JoevarApi
web_repo=Knucklehead88/joevarWeb

# Secrets go through a private temporary file (never the command line or the screen) and are deleted on exit.
umask 077
params=$(mktemp)
trap 'rm -f "$params"' EXIT

# The API's Twilio, Email and Google settings come from this machine's user secrets. A new database password is
# generated on every run; the API's connection string is updated with it.
dotnet user-secrets list --json | python3 -c '
import json, secrets, sys
lines = [line for line in sys.stdin.read().splitlines() if not line.startswith("//")]
user_secrets = json.loads("\n".join(lines) or "{}")
api_secrets = {key.replace(":", "__"): value for key, value in user_secrets.items()
               if key.split(":")[0] in ("Twilio", "Email", "Google")}
password = "Jv1-" + secrets.token_urlsafe(24)
json.dump({
    "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
    "contentVersion": "1.0.0.0",
    "parameters": {
        "location": {"value": sys.argv[1]},
        "apiName": {"value": sys.argv[2]},
        "webName": {"value": sys.argv[3]},
        "databaseServerName": {"value": sys.argv[4]},
        "databaseAdminPassword": {"value": password},
        "apiSecrets": {"value": api_secrets},
    },
}, sys.stdout)
' "$location" "$api_name" "$web_name" "$database_server" > "$params"

echo "Creating or updating the resources in $group ($location); the first run takes about 10 minutes..."
az group create --name "$group" --location "$location" --output none
# Run on its own (not inside a pipe or <(...)), so a failed deployment stops the script here.
outputs=$(az deployment group create \
  --resource-group "$group" \
  --name joevar \
  --template-file infra/main.bicep \
  --parameters @"$params" \
  --query '[properties.outputs.apiUrl.value, properties.outputs.webUrl.value]' \
  --output tsv)
api_url=$(sed -n 1p <<< "$outputs")
web_url=$(sed -n 2p <<< "$outputs")

echo "API:     $api_url"
echo "Website: $web_url"

google_client_id=$(dotnet user-secrets list --json | python3 -c '
import json, sys
lines = [line for line in sys.stdin.read().splitlines() if not line.startswith("//")]
print(json.loads("\n".join(lines) or "{}").get("Google:ClientId", ""))')

if command -v gh > /dev/null && gh auth status > /dev/null 2>&1; then
  echo "Connecting GitHub Actions..."
  az webapp deployment list-publishing-profiles --resource-group "$group" --name "$api_name" --xml \
    | gh secret set AZURE_API_PUBLISH_PROFILE --repo "$api_repo"
  gh variable set AZURE_API_APP_NAME --repo "$api_repo" --body "$api_name"
  az staticwebapp secrets list --resource-group "$group" --name "$web_name" --query properties.apiKey --output tsv \
    | gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --repo "$web_repo"
  gh variable set API_BASE_URL --repo "$web_repo" --body "$api_url"
  gh variable set GOOGLE_CLIENT_ID --repo "$web_repo" --body "$google_client_id"
  echo "Done. Push (or re-run the workflows) to deploy."
else
  cat <<EOF

gh isn't signed in, so set these in GitHub (Settings > Secrets and variables > Actions) by hand:
  $api_repo  secret   AZURE_API_PUBLISH_PROFILE        = output of: az webapp deployment list-publishing-profiles -g $group -n $api_name --xml
  $api_repo  variable AZURE_API_APP_NAME               = $api_name
  $web_repo  secret   AZURE_STATIC_WEB_APPS_API_TOKEN  = output of: az staticwebapp secrets list -g $group -n $web_name --query properties.apiKey -o tsv
  $web_repo  variable API_BASE_URL                     = $api_url
  $web_repo  variable GOOGLE_CLIENT_ID                 = $google_client_id
EOF
fi
