// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

public class OrleansResource(string name) : Resource(name)
{
    public IResourceBuilder<IResourceWithConnectionString>? Clustering { get; set; }
    public IResourceBuilder<IResourceWithConnectionString>? Reminders { get; set; }
    public Dictionary<string, IResourceBuilder<IResourceWithConnectionString>> GrainStorage { get; } = new();
}