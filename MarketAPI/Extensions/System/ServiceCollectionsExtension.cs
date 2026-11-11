using System.Text;
using MarketAPI.Data;
using MarketAPI.Options;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using MarketAPI.Services;

namespace MarketAPI.Extensions.System;
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRequiredServices(this IServiceCollection services)
    {
        // --------------------------------------------------------------------------------
        // -------------------------- Services ----------------------------------
        // --------------------------------------------------------------------------------
        // Scheduled background operations
        services.AddHostedService<MarketCollectionWorker>();
        services.AddHostedService<MarketRetentionWorker>();


        services.AddScoped<MarketIngestionService>();
        services.AddScoped<MarketRetentionService>();
        services.AddScoped<MarketArchivePlanningService>();
        services.AddScoped<MarketArchiveLedgerService>();
        services.AddScoped<MarketArchiveBackupService>();
        services.AddScoped<MarketArchiveExtractionService>();
        services.AddScoped<MarketArchivePublicationService>();

        services.AddSingleton<SqliteMaintenanceService>();
        services.AddSingleton<DatabaseWriteCoordinator>();
        services.AddSingleton<SqliteMarketBulkWriter>();

        services.AddScoped<MarketAnalyticsService>();
        
        services.AddHttpClient<OsrsWikiClient>(client =>
        {
            client.BaseAddress = new Uri(
                "https://prices.runescape.wiki/api/v2/osrs/");

            // Each attempt gets up to 20 seconds.
            // The client performs at most 3 attempts.
            client.Timeout = TimeSpan.FromSeconds(20);

            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "NuggetCapital-MarketAPI/0.1 (contact: neothej1sec@gmail.com)");
        });
        services.AddMemoryCache();
        return services;
    }

    public static IServiceCollection AddDataStores(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<SqlitePragmaInterceptor>();
        
        var marketSupportConnection = config.GetConnectionString("MarketDb")
            ?? throw new InvalidOperationException("Connection string 'MarketDb' was not found.");

        services.AddDbContextFactory<MarketDbContext>((sp, options) =>
        {
            options.UseSqlite(marketSupportConnection);

            options.AddInterceptors(
                sp.GetRequiredService<SqlitePragmaInterceptor>());
        });

        
        return services;
    }

    public static IServiceCollection AddControllers(this IServiceCollection services, IConfiguration config)
    {
        services.AddMemoryCache();
        services.AddControllers();
        services.AddEndpointsApiExplorer();


        return services;
    }

    public static IServiceCollection AddAppSettings(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddMemoryCache();

        services.Configure<MarketStorageOptions>(
            config.GetSection("MarketStorage"));

        services.Configure<MarketCollectorOptions>(
            config.GetSection("MarketCollector"));

        return services;
    }



    // public static IServiceCollection AddAuthPolicies(this IServiceCollection services, IConfiguration config)
    // {
    //     // var tokenKey = config["TokenKey"]
    //     //     ?? throw new InvalidOperationException("TokenKey is missing from configuration.");
    //     services.Configure<JwtOptions>(config.GetSection("Jwt"));
    //     services.Configure<MySettingsOptions>(config.GetSection("MySettings"));

    //     var jwt = config.GetSection("Jwt").Get<JwtOptions>()
    //         ?? throw new InvalidOperationException("Jwt configuration is missing.");

    //     if (string.IsNullOrWhiteSpace(jwt.SigningKey))
    //         throw new InvalidOperationException("Jwt:SigningKey is missing from configuration.");
    //     // services.AddIdentityCore<AppUser>(opt =>
    //     // {
    //     //     opt.Password.RequireNonAlphanumeric = false;
    //     // })
    //     // .AddRoles<AppRole>()
    //     // .AddRoleManager<RoleManager<AppRole>>()
    //     // .AddSignInManager<SignInManager<AppUser>>()
    //     // .AddRoleValidator<RoleValidator<AppRole>>()
    //     // .AddEntityFrameworkStores<EnvisiradDBContext>();
        
    //     services.AddAuthentication(x =>
    //     {
    //         x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    //         x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    //     })
    //     .AddJwtBearer(options =>
    //     {
    //         options.TokenValidationParameters = new TokenValidationParameters
    //         {
    //             ValidateIssuerSigningKey = true,
    //             IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
    //             ValidateIssuer = false,
    //             ValidateAudience = false,
    //             ValidateLifetime = true,
    //             ClockSkew = TimeSpan.Zero,
    //             RoleClaimType = ClaimTypes.Role,
    //             NameClaimType = ClaimTypes.Name
    //         };

    //         options.Events = new JwtBearerEvents
    //         {
    //             OnTokenValidated = context =>
    //             {
    //                 var blacklist = context.HttpContext.RequestServices
    //                     .GetRequiredService<IJwtTokenService>();
                        
    //                 if (context.Principal is null)
    //                 {
    //                     context.Fail("Missing principal.");
    //                     return Task.CompletedTask;
    //                 }
    //                 if (blacklist.IsRevoked(context.Principal!))
    //                 {
    //                     context.Fail("Token has been revoked.");
    //                 }

    //                 return Task.CompletedTask;
    //             }
    //         };
    //     });
    //     services.AddAuthorization(opt =>
    //     {//[Authorize(Roles = "CSA,Integration")]
    //         // opt.AddPolicy(AppPolicies.RequireAdmin, policy => policy.RequireRole("Admin"));
    //         opt.AddPolicy(AppPolicies.RequireIntegration, policy => policy.RequireRole("Integration", "Admin"));
    //         opt.AddPolicy(AppPolicies.RequireCSA, policy => policy.RequireRole("CSA","Integration","Admin"));
    //         opt.AddPolicy(AppPolicies.RequireTech, policy => policy.RequireRole("Tech","CSA", "Integration", "Admin"));
    //         opt.AddPolicy(AppPolicies.RequireIntCsaRevRole, policy => policy.RequireRole("Rev Cycle Lead", "CSA", "Integration", "Admin"));
    //         opt.AddPolicy(AppPolicies.RequireRevLead, policy => policy.RequireRole("Rev Cycle Lead", "Integration", "Admin"));
    //         opt.AddPolicy(AppPolicies.RequireRevCycle, policy => policy.RequireRole("Rev Cycle Coder", "Integration", "Admin"));
    //         opt.AddPolicy(AppPolicies.RequireDowntime, policy => policy.RequireRole("Downtime User", "Integration", "Admin"));
    //     });

    //     return services;
    // }

    // public static IServiceCollection AddInboundQueue(this IServiceCollection services, IConfiguration config)
    // {
    //     services.AddOptions<InboundQueueOptions>()
    //         .Bind(config.GetSection("InboundQueue"))
    //         .Validate(o => o.DefaultClaimBatchSize is >= 1 and <= 500, "InboundQueue:DefaultClaimBatchSize must be 1..500.")
    //         .Validate(o => o.LeaseDuration > TimeSpan.Zero, "InboundQueue:LeaseDuration must be > 0.")
    //         .Validate(o => o.RequeueSweepInterval > TimeSpan.Zero, "InboundQueue:RequeueSweepInterval must be > 0.")
    //         .Validate(o => o.MaxPending >= 0, "InboundQueue:MaxPending must be >= 0 (0 = unlimited).")
    //         .Validate(o => o.MaxDbSizeMb >= 0, "InboundQueue:MaxDbSizeMb must be >= 0 (0 = unlimited).")
    //         .Validate(o => o.HeartbeatInterval >= TimeSpan.Zero, "InboundQueue:HeartbeatInterval must be >= 0 (00:00:00 disables).")
    //         .ValidateOnStart();

    //     services.AddOptions<InboundRetentionOptions>()
    //         .Bind(config.GetSection("InboundRetention"))
    //         .ValidateOnStart();

    //     services.AddOptions<MllpListenerOptions>()
    //         .Bind(config.GetSection("MllpListener"))
    //         .ValidateOnStart();
        
    //     services.AddOptions<InboundProcessingOptions>()
    //         .Bind(config.GetSection("InboundProcessing"))
    //         .Validate(o => o.BatchSize is >= 1 and <= 500, "InboundProcessing:BatchSize must be 1..500.")
    //         .Validate(o => o.RequeueInterval > TimeSpan.Zero, "InboundProcessing:RequeueInterval must be > 0.")
    //         .Validate(o => o.RenewWhenRemaining > TimeSpan.Zero, "InboundProcessing:RenewWhenRemaining must be > 0.")
    //         .Validate(o => o.ErrorDelay > TimeSpan.Zero, "InboundProcessing:ErrorDelay must be > 0.")
    //         .ValidateOnStart();
            
    //     services.AddScoped<IInboundMessageRepository, InboundMessageRepository>();
    //     services.AddScoped<IInboundMessageService, InboundMessageService>();
    //     services.AddSingleton<IWorkerIdentity, WorkerIdentity>();
    //     services.AddScoped<IInboundMessageService, InboundMessageService>();
        

    //     return services;
    // }
}



