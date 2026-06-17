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
    public static async Task EnsureTableAsync(IAmazonDynamoDB db, string tableName, CancellationToken ct = default)
    {
        if (await IsActiveAsync(db, tableName, ct).ConfigureAwait(false))
            return;

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
                return;
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
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
