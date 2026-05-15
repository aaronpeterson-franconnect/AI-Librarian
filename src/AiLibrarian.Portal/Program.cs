using AiLibrarian.Portal.Components;
using AiLibrarian.Portal.Services;

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);

builder.Services
	.AddRazorComponents()
	.AddInteractiveServerComponents(options =>
	{
		// Surface server-side exceptions to the client transcript in
		// Development so circuit terminations include the real stack
		// instead of the generic "unhandled exception ... will be
		// terminated" boilerplate. Stays off in non-Development.
		options.DetailedErrors = builder.Environment.IsDevelopment();
	});

builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.SectionName));
builder.Services.Configure<PortalDownstreamApiOptions>(builder.Configuration.GetSection(PortalDownstreamApiOptions.SectionName));

// Entra opt-in. When AzureAd:ClientId is configured, switch the portal
// into "real sign-in" mode: OIDC auth on the browser side, OBO token
// acquisition + bearer attachment on the HttpClient side, claims-based
// contributor identity. Empty AzureAd:ClientId keeps the existing
// dev-mode dropdown / DefaultContributorId path live so the pilot
// continues to work without any tenant.
var azureAdSection = builder.Configuration.GetSection("AzureAd");
var entraConfigured = !string.IsNullOrWhiteSpace(azureAdSection["ClientId"]);

builder.Services.AddHttpContextAccessor();

if (entraConfigured)
{
	builder.Services
		.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
		.AddMicrosoftIdentityWebApp(azureAdSection)
		.EnableTokenAcquisitionToCallDownstreamApi(
			builder.Configuration.GetSection(PortalDownstreamApiOptions.SectionName).GetSection("Scopes").Get<string[]>() ?? Array.Empty<string>())
		.AddInMemoryTokenCaches();

	builder.Services.AddAuthorization();
	builder.Services.AddCascadingAuthenticationState();

	// Microsoft.Identity.Web.UI exposes the /MicrosoftIdentity/Account/{SignIn,SignOut} endpoints.
	builder.Services
		.AddControllersWithViews()
		.AddMicrosoftIdentityUI();

	builder.Services.AddScoped<IPortalUserContext, EntraPortalUserContext>();
	builder.Services.AddTransient<AuthenticationDelegatingHandler>();
}
else
{
	// Dev-mode fallback: scoped DevPortalUserContext, no auth.
	builder.Services.AddScoped<IPortalUserContext, DevPortalUserContext>();
}

var apiBase = builder.Configuration["Api:BaseUrl"]?.Trim();
if (string.IsNullOrEmpty(apiBase))
{
	apiBase = "http://localhost:5071/";
}

if (!apiBase.EndsWith('/'))
{
	apiBase += "/";
}

var apiClientBuilder = builder.Services.AddHttpClient(
	"Api",
	client => client.BaseAddress = new Uri(apiBase));

if (entraConfigured)
{
	apiClientBuilder.AddHttpMessageHandler<AuthenticationDelegatingHandler>();
}

builder.Services.AddScoped<IAiLibrarianApiClient, AiLibrarianApiClient>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

if (entraConfigured)
{
	app.UseAuthentication();
	app.UseAuthorization();
}

app.UseAntiforgery();

app.MapStaticAssets();

// Microsoft.Identity.Web.UI controller endpoints (sign-in / sign-out).
if (entraConfigured)
{
	app.MapControllers();
}

app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode();

app.Run();
