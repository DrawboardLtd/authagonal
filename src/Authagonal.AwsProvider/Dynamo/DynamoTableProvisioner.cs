using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Authagonal.AwsProvider.Dynamo;

/// <summary>
/// Creates the pk(HASH)+sk(RANGE) on-demand tables the AWS stores need, mirroring how the Azure
/// package eagerly calls <c>CreateIfNotExists</c> at registration. Idempotent: when the table already
/// exists (e.g. provisioned by Terraform) it just confirms it's ACTIVE and returns. Newly created
/// tables are polled until ACTIVE so the first request after boot doesn't race table creation.
/// </summary>
public static class DynamoTableProvisioner
{
    /// <summary>The attribute DynamoDB reads for time-to-live: epoch seconds, per the API.</summary>
    public const string TtlAttribute = "ttl";

    /// <param name="expiring">
    /// Enable DynamoDB TTL on <see cref="TtlAttribute"/> for this table.
    /// </param>
    /// <remarks>
    /// No table had TTL and the package ships no reaper, so every transient security row — OIDC
    /// federation state (written by an anonymous endpoint on every hit), MFA challenges, the
    /// revocation list, upstream refresh tokens — was retained forever. Every read path already fails
    /// closed on age, so this was never a replay hole; it is unbounded retention of sensitive rows,
    /// including upstream refresh tokens, which are live credentials for another IdP.
    ///
    /// Deliberately NOT applied to the SAML assertion-replay table: those rows must outlive the
    /// longest assertion the server will accept, and expiring them early is a real replay hole rather
    /// than a storage saving.
    /// </remarks>
    public static async Task EnsureTableAsync(
        IAmazonDynamoDB db, string tableName, bool expiring = false, CancellationToken ct = default)
    {
        if (await IsActiveAsync(db, tableName, ct).ConfigureAwait(false))
        {
            // Applied to pre-existing tables too (Terraform, a previous version of this code), because
            // the tables that need it were created without it.
            if (expiring) await EnableTtlAsync(db, tableName, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await db.CreateTableAsync(new CreateTableRequest
            {
                TableName = tableName,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                KeySchema =
                [
                    new KeySchemaElement(Dyn.Pk, KeyType.HASH),
                    new KeySchemaElement(Dyn.Sk, KeyType.RANGE),
                ],
                AttributeDefinitions =
                [
                    new AttributeDefinition(Dyn.Pk, ScalarAttributeType.S),
                    new AttributeDefinition(Dyn.Sk, ScalarAttributeType.S),
                ],
            }, ct).ConfigureAwait(false);
        }
        catch (ResourceInUseException)
        {
            // Created concurrently by another pod — fall through to the ACTIVE wait.
        }

        // Newly created tables take a few seconds to become ACTIVE.
        for (var i = 0; i < 60; i++)
        {
            if (await IsActiveAsync(db, tableName, ct).ConfigureAwait(false))
            {
                if (expiring) await EnableTtlAsync(db, tableName, ct).ConfigureAwait(false);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Idempotently turns TTL on. Reads the current setting first, because UpdateTimeToLive throws
    /// when the requested state is the one already in force.
    /// </summary>
    private static async Task EnableTtlAsync(IAmazonDynamoDB db, string tableName, CancellationToken ct)
    {
        try
        {
            var current = await db.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = tableName }, ct)
                .ConfigureAwait(false);
            var status = current.TimeToLiveDescription?.TimeToLiveStatus;
            if (status == TimeToLiveStatus.ENABLED || status == TimeToLiveStatus.ENABLING)
                return;

            await db.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = tableName,
                TimeToLiveSpecification = new TimeToLiveSpecification { AttributeName = TtlAttribute, Enabled = true },
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AmazonDynamoDBException or NotSupportedException)
        {
            // A deployment whose credentials cannot alter table settings, or a local emulator that
            // does not implement the TTL API, must still start. Retention is then unbounded, which is
            // exactly the state before this change — not a regression, and the stores write the
            // attribute regardless so enabling TTL later takes effect immediately.
        }
    }

    private static async Task<bool> IsActiveAsync(IAmazonDynamoDB db, string tableName, CancellationToken ct)
    {
        try
        {
            var resp = await db.DescribeTableAsync(tableName, ct).ConfigureAwait(false);
            return resp.Table?.TableStatus == TableStatus.ACTIVE;
        }
        catch (ResourceNotFoundException)
        {
            return false;
        }
    }
}
