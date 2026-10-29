using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lookout.Api.Data;

/// <summary>Lets `dotnet ef migrations add` build the model without starting the web host.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<LookoutDb>
{
    public LookoutDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LookoutDb>()
            .UseNpgsql("Host=localhost;Database=lookout", o => o.UseNetTopologySuite())
            .Options);
}
