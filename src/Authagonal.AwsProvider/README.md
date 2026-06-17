# Authagonal.AwsProvider

AWS implementation of the Authagonal storage and clustering seams — the counterpart to
`Authagonal.Storage` (Azure). All cloud-vendor coupling lives here; `Authagonal.Core` and
`Authagonal.Protocol` stay vendor-neutral.

| Authagonal seam | AWS service | Notes |
|---|---|---|
| Stores (`IUserStore`, `IGrantStore`, `ISigningKeyStore`, …) | **DynamoDB** | One table per Azure table; composite key `pk`+`sk` mirrors PartitionKey/RowKey. |
| Leader election (`ILeaseProvider`) | **DynamoDB** conditional-write lease | No native lease primitive; a conditional `PutItem` gives single-holder semantics. |
| Cluster event bus (`IClusterEventBus`) | **DynamoDB** append-log + poll | Mirrors the Azure table-log bus. At-least-once, unordered. |
| Secrets (`ISecretProvider`) | **Secrets Manager** | Substitute for Azure Key Vault; references stored as `sm:{name}`. |

Single-use grant redemption uses a conditional `DeleteItem` (`attribute_exists` + `ReturnValues=ALL_OLD`)
in place of Azure's ETag/`If-Match` delete — the same anti-replay guarantee. The grant expiry index is
re-keyed (`pk = exp_{shard}`, `sk = {yyyy-MM-dd}#{hashedKey}`) so the cleanup sweep can range-scan the
sort key, since DynamoDB cannot range-query a partition key.

Credentials resolve via the standard AWS chain (env / EC2 instance role / IRSA), so there's no
managed-identity-vs-connection-string split — pass a configured `IAmazonDynamoDB`.

```csharp
services.AddDynamoStorage(dynamoDbClient);
services.AddSecretsManager(secretsManagerClient);
// clustering:
builder.UseAwsDynamo(dynamoDbClient);        // auth nodes (leader + bus)
builder.UseAwsDynamoBus(dynamoDbClient);     // portal/admin nodes (bus only)
```

> **Status:** foundation + `IClientStore`, `ISigningKeyStore`, `IGrantStore`, clustering, and secrets
> are implemented. The remaining stores (user, MFA, OIDC/SAML/SSO, SCIM, roles, scopes, revoked tokens,
> provisioning apps, user provisions) are a mechanical port of the same patterns.
