using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderFlow.Infrastructure.Persistence;

public sealed class OrderFlowDbContextFactory : IDesignTimeDbContextFactory<OrderFlowDbContext>
{
    public OrderFlowDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ORDERFLOW_SQL_CONNECTION")
            ?? "Server=localhost;Database=OrderFlow;Integrated Security=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<OrderFlowDbContext>().UseSqlServer(connectionString).Options;
        return new OrderFlowDbContext(options);
    }
}
