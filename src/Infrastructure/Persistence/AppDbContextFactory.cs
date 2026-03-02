using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WhatsAppSaaS.Infrastructure.Persistence
{
    public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

            var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

            if (!string.IsNullOrWhiteSpace(databaseUrl))
            {
                var connString = ConvertDatabaseUrlToNpgsql(databaseUrl);
                optionsBuilder.UseNpgsql(connString);
            }
            else
            {
                // Fallback local (solo para dev)
                optionsBuilder.UseSqlite("Data Source=app.db");
            }

            return new AppDbContext(optionsBuilder.Options);
        }

        private static string ConvertDatabaseUrlToNpgsql(string databaseUrl)
        {
            var uri = new Uri(databaseUrl);

            var userInfo = uri.UserInfo.Split(':', 2);
            var username = Uri.UnescapeDataString(userInfo[0]);
            var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

            var host = uri.Host;

            // ✅ IMPORTANTE: si no viene puerto, usamos 5432
            var port = uri.Port > 0 ? uri.Port : 5432;

            var database = uri.AbsolutePath.TrimStart('/');

            return $"Host={host};Port={port};Database={database};Username={username};Password={password};Ssl Mode=Require;Trust Server Certificate=true";
        }
    }
}
