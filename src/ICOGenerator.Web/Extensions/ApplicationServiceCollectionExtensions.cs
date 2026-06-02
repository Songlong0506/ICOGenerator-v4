using ICOGenerator.Application.DependencyInjection;
using ICOGenerator.Infrastructure.DependencyInjection;

namespace ICOGenerator.Extensions;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddIcoGeneratorApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllersWithViews();
        services.AddIcoGeneratorApplicationUseCases();
        services.AddIcoGeneratorInfrastructure(configuration);

        return services;
    }
}
