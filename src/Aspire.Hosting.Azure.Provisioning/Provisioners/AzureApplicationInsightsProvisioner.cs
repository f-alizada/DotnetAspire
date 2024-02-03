// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Azure.ResourceManager.ApplicationInsights;
using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Provisioning;

internal sealed class AzureApplicationInsightsProvisioner(ILogger<AzureApplicationInsightsProvisioner> logger) : AzureResourceProvisioner<AzureApplicationInsightsResource>
{
    public override bool ConfigureResource(IConfiguration configuration, AzureApplicationInsightsResource resource)
    {
        if (configuration.GetConnectionString(resource.Name) is string connectionString)
        {
            resource.ConnectionString = connectionString;
            return true;
        }

        return false;
    }

    public override async Task GetOrCreateResourceAsync(AzureApplicationInsightsResource resource, ProvisioningContext context, CancellationToken cancellationToken)
    {
        context.ResourceMap.TryGetValue(resource.Name, out var azureResource);

        if (azureResource is not null && azureResource is not ApplicationInsightsComponentResource)
        {
            logger.LogWarning("Resource {resourceName} is not an application insights resource. Deleting it.", resource.Name);

            await context.ArmClient.GetGenericResource(azureResource.Id).DeleteAsync(WaitUntil.Started, cancellationToken).ConfigureAwait(false);
        }

        var applicationInsightsResource = azureResource as ApplicationInsightsComponentResource;

        if (applicationInsightsResource is null)
        {
            var applicationInsightsName = Guid.NewGuid().ToString().Replace("-", string.Empty)[0..20];

            // We could model application insights as a child resource of a log analytics workspace, but instead,
            // we'll just create it on demand.

            logger.LogInformation("Creating application insights {applicationInsightsName} in {location}...", applicationInsightsName, context.Location);

            var applicationInsightsCreateOrUpdateContent = new ApplicationInsightsComponentData(context.Location, "web")
            {
                WorkspaceResourceId = ""// context.LogAnalyticsWorkspace.Id
            };
            applicationInsightsCreateOrUpdateContent.Tags.Add(AzureProvisioner.AspireResourceNameTag, resource.Name);

            var sw = Stopwatch.StartNew();
            var operation = await context.ResourceGroup.GetApplicationInsightsComponents().CreateOrUpdateAsync(WaitUntil.Completed, applicationInsightsName, applicationInsightsCreateOrUpdateContent, cancellationToken).ConfigureAwait(false);
            applicationInsightsResource = operation.Value;
            sw.Stop();

            logger.LogInformation("Application Insights {applicationInsightsName} created in {elapsed}", applicationInsightsResource.Data.Name, sw.Elapsed);
        }

        resource.ConnectionString = applicationInsightsResource.Data.ConnectionString;

        var connectionStrings = context.UserSecrets.Prop("ConnectionStrings");
        connectionStrings[resource.Name] = resource.ConnectionString;
    }
}