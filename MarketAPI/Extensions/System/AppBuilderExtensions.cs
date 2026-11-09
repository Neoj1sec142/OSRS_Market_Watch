using MarketAPI.Data;

namespace MarketAPI.Extensions.System;

public static class AppBuilderExtensions
{
    private const string DevCorsPolicy = "DevCors";
    public static WebApplication BuildMarketWatch()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddDataStores(builder.Configuration);
        builder.Services.AddControllers(builder.Configuration);
        builder.Services.AddRequiredServices();
        builder.Services.AddAppSettings(builder.Configuration);
        builder.Services.AddCors(builder.Configuration);
        // builder.Services.AddEnvisiradAuthPolicies(builder.Configuration);
        return builder.Build();
    }

    public static async Task<WebApplication> InitializeMarketWatchAsync(
        this WebApplication app,
        CancellationToken ct = default)
    {
        var connectionString = app.Configuration.GetConnectionString("MarketDb")
            ?? throw new InvalidOperationException(
                "Connection string 'MarketDb' was not found.");

        await SqliteDatabaseInitializer.InitializeAsync(
            app.Services,
            connectionString,
            ct);

        return app;
    }

    public static WebApplication UseMarketWatch(this WebApplication app)
    {
        // app.UseSwagger();
        // app.UseSwaggerUI();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHttpsRedirection();
        }
        app.UseCors();
        

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();

        return app;
    }

    private static IServiceCollection AddCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddCors(options =>
        {
            options.AddPolicy(DevCorsPolicy, policy =>
            {
                policy
                    // .WithOrigins(
                    //     "http://localhost:4200",
                    //     "https://localhost:4200",
                    //     "http://127.0.0.1:4200",
                    //     "https://127.0.0.1:4200"
                    // )
                    .AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        return services;
    }

    private static WebApplication UseCors(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.UseCors(DevCorsPolicy);
        }

        return app;
    }
}