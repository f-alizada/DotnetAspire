// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Orleans.Server;

namespace Aspire.Hosting;

/// <summary>
/// Extensions to <see cref="IDistributedApplicationBuilder"/> related to Orleans.
/// </summary>
public static class IDistributedApplicationBuilderExtensions
{
    private const string OrleansConfigKeyEnvVarPrefix = "Grains";

    public static IResourceBuilder<OrleansResource> AddOrleans(
        this IDistributedApplicationBuilder builder,
        string name)
        => builder.AddResource(new OrleansResource(name));

    public static IResourceBuilder<OrleansResource> WithClustering(
        this IResourceBuilder<OrleansResource> builder,
        IResourceBuilder<IResourceWithConnectionString> clustering)
    {
        builder.Resource.Clustering = clustering;
        return builder;
    }

    public static IResourceBuilder<OrleansResource> WithGrainStorage(
        this IResourceBuilder<OrleansResource> builder,
        IResourceBuilder<IResourceWithConnectionString> storage)
    {
        builder.Resource.GrainStorage[storage.Resource.Name] = storage;
        return builder;
    }

    public static IResourceBuilder<OrleansResource> WithGrainStorage(
        this IResourceBuilder<OrleansResource> builder,
        string name,
        IResourceBuilder<IResourceWithConnectionString> storage)
    {
        builder.Resource.GrainStorage[name] = storage;
        return builder;
    }

    public static IResourceBuilder<OrleansResource> WithReminders(
        this IResourceBuilder<OrleansResource> builder,
        IResourceBuilder<IResourceWithConnectionString> reminderStorage)
    {
        builder.Resource.Reminders = reminderStorage;
        return builder;
    }

    public static IResourceBuilder<T> WithOrleansServer<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<OrleansResource> orleansResourceBuilder)
        where T : IResourceWithEnvironment
    {
        var res = orleansResourceBuilder.Resource;
        foreach (var (name, storage) in res.GrainStorage)
        {
            builder.WithReference(storage);
            builder.WithEnvironment($"{OrleansConfigKeyEnvVarPrefix}__GrainStorage__{name}__ConnectionType", GetResourceType(storage));
            builder.WithEnvironment($"{OrleansConfigKeyEnvVarPrefix}__GrainStorage__{name}__ConnectionName", storage.Resource.Name);
        }

        if (res.Reminders is { } reminders)
        {
            builder.WithReference(reminders);
            builder.WithEnvironment($"{OrleansConfigKeyEnvVarPrefix}__Reminders__ConnectionType", GetResourceType(reminders));
            builder.WithEnvironment($"{OrleansConfigKeyEnvVarPrefix}__Reminders__ConnectionName", reminders.Resource.Name);
        }

        // Configure clustering
        var clustering = res.Clustering ?? throw new InvalidOperationException("Clustering has not been configured for this service.");
        builder.WithReference(clustering);
        builder.WithEnvironment($"{OrleansConfigKeyEnvVarPrefix}__Clustering__ConnectionType", GetResourceType(clustering));
        builder.WithEnvironment($"{OrleansConfigKeyEnvVarPrefix}__Clustering__ConnectionName", clustering.Resource.Name);

        return builder;
    }

    public static IResourceBuilder<T> WithOrleansClient<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<OrleansResource> orleansResourceBuilder)
        where T : IResourceWithEnvironment
    {
        return builder
            .WithReference(orleansResourceBuilder.Resource.Clustering!);
    }

    private static string? GetResourceType(IResourceBuilder<IResource> resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource switch
        {
            IResourceBuilder<AzureTableStorageResource> => OrleansServerSettingConstants.AzureTablesType,
            IResourceBuilder<AzureBlobStorageResource> => OrleansServerSettingConstants.AzureBlobsType,
            IResourceBuilder<OrleansResource> => OrleansServerSettingConstants.InternalType,
            _ => throw new NotSupportedException($"Resources of type '{resource.GetType()}' are not supported.")
        };
    }
}