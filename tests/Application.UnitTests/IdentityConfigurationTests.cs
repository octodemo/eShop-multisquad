using eShop.Identity.API.Configuration;
using eShop.Identity.API.Services;
using Microsoft.Extensions.Configuration;

namespace eShop.Application.UnitTests;

[TestClass]
public class IdentityConfigurationTests
{
    [TestMethod]
    public void ApiResourcesAndScopesStayAligned()
    {
        var apiResources = Config.GetApis().ToArray();
        var resources = apiResources.Select(resource => resource.Name).Order().ToArray();
        var scopes = Config.GetApiScopes().Select(scope => scope.Name).Order().ToArray();

        CollectionAssert.AreEqual(resources, scopes);
        CollectionAssert.AreEquivalent(new[] { "basket", "orders", "webhooks", "wishlist" }, resources);
        CollectionAssert.AreEquivalent(new[] { "basket", "orders", "webhooks", "wishlist" }, scopes);
        Assert.AreEqual(resources.Length, resources.Distinct().Count());
        Assert.AreEqual(scopes.Length, scopes.Distinct().Count());
        CollectionAssert.AreEquivalent(new[] { "wishlist" }, apiResources.Single(resource => resource.Name == "wishlist").Scopes.ToArray());
    }

    [TestMethod]
    public void ClientsUseConfiguredCallbackUrlsAndExpectedScopes()
    {
        var values = new Dictionary<string, string?>
        {
            ["MauiCallback"] = "maui://callback",
            ["WebAppClient"] = "https://webapp.test",
            ["WebhooksWebClient"] = "https://webhooks-client.test",
            ["BasketApiClient"] = "https://basket.test",
            ["OrderingApiClient"] = "https://ordering.test",
            ["WebhooksApiClient"] = "https://webhooks.test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var clients = Config.GetClients(configuration).ToDictionary(client => client.ClientId);

        CollectionAssert.Contains(clients["webapp"].RedirectUris.ToList(), "https://webapp.test/signin-oidc");
        CollectionAssert.Contains(clients["webhooksclient"].AllowedScopes.ToList(), "webhooks");
        CollectionAssert.Contains(clients["maui"].AllowedScopes.ToList(), "basket");
        CollectionAssert.Contains(clients["maui"].AllowedScopes.ToList(), "orders");
    }

    [TestMethod]
    public void WishlistPermissionIsRestrictedToWebApp()
    {
        var values = new Dictionary<string, string?>
        {
            ["MauiCallback"] = "maui://callback",
            ["WebAppClient"] = "https://webapp.test",
            ["WebhooksWebClient"] = "https://webhooks-client.test",
            ["BasketApiClient"] = "https://basket.test",
            ["OrderingApiClient"] = "https://ordering.test",
            ["WebhooksApiClient"] = "https://webhooks.test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var clients = Config.GetClients(configuration).ToArray();
        var webapp = clients.Single(client => client.ClientId == "webapp");

        Assert.AreEqual(1, webapp.AllowedScopes.Count(scope => scope == "wishlist"));
        foreach (var client in clients.Where(client => client.ClientId != "webapp"))
        {
            Assert.AreEqual(0, client.AllowedScopes.Count(scope => scope == "wishlist"), client.ClientId);
        }
    }

    [TestMethod]
    public void WishlistRegistrationPreservesExistingPermissions()
    {
        var values = new Dictionary<string, string?>
        {
            ["MauiCallback"] = "maui://callback",
            ["WebAppClient"] = "https://webapp.test",
            ["WebhooksWebClient"] = "https://webhooks-client.test",
            ["BasketApiClient"] = "https://basket.test",
            ["OrderingApiClient"] = "https://ordering.test",
            ["WebhooksApiClient"] = "https://webhooks.test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var expectedScopes = new Dictionary<string, string[]>
        {
            ["webapp"] = new[] { "openid", "profile", "offline_access", "orders", "basket", "webshoppingagg", "webhooks", "wishlist" },
            ["maui"] = new[] { "openid", "profile", "offline_access", "orders", "basket", "mobileshoppingagg", "webhooks" },
            ["webhooksclient"] = new[] { "openid", "profile", "offline_access", "webhooks" },
            ["basketswaggerui"] = new[] { "basket" },
            ["orderingswaggerui"] = new[] { "orders" },
            ["webhooksswaggerui"] = new[] { "webhooks" }
        };

        var clients = Config.GetClients(configuration).ToArray();

        CollectionAssert.AreEquivalent(expectedScopes.Keys.ToArray(), clients.Select(client => client.ClientId).ToArray());
        foreach (var client in clients)
        {
            var scopes = client.AllowedScopes.ToArray();
            CollectionAssert.AreEquivalent(expectedScopes[client.ClientId], scopes, client.ClientId);
            Assert.AreEqual(scopes.Length, scopes.Distinct().Count(), client.ClientId);
        }

        var webapp = clients.Single(client => client.ClientId == "webapp");
        CollectionAssert.AreEquivalent(new[] { "authorization_code" }, webapp.AllowedGrantTypes.ToArray());
        CollectionAssert.AreEquivalent(new[] { "https://webapp.test/signin-oidc" }, webapp.RedirectUris.ToArray());
        CollectionAssert.AreEquivalent(new[] { "https://webapp.test/signout-callback-oidc" }, webapp.PostLogoutRedirectUris.ToArray());
        Assert.IsFalse(webapp.AllowAccessTokensViaBrowser);

        CollectionAssert.AreEquivalent(new[] { "openid", "profile" }, Config.GetResources().Select(resource => resource.Name).ToArray());

        var legacyResources = Config.GetApis().Where(resource => resource.Name != "wishlist").ToArray();
        CollectionAssert.AreEquivalent(new[] { "basket", "orders", "webhooks" }, legacyResources.Select(resource => resource.Name).ToArray());
        foreach (var resource in legacyResources)
        {
            Assert.AreEqual(0, resource.Scopes.Count, resource.Name);
        }
    }

    [TestMethod]
    [DataRow("/connect/authorize?redirect_uri=https%3A%2F%2Fweb.test%2Fsignin-oidc&scope=openid", "https://web.test/")]
    [DataRow("/connect/authorize?client_id=webapp", "")]
    public void RedirectServiceExtractsConfiguredRedirect(string returnUrl, string expected)
    {
        var service = new RedirectService();

        Assert.AreEqual(expected, service.ExtractRedirectUriFromReturnUrl(returnUrl));
    }
}
