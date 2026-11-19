using System.Security.Claims;
using eShop.Basket.API.Repositories;
using eShop.Basket.API.Grpc;
using eShop.Basket.API.IntegrationEvents.EventHandling;
using eShop.Basket.API.IntegrationEvents.EventHandling.Events;
using eShop.Basket.API.Model;
using eShop.Basket.UnitTests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Grpc.Core;
using BasketItem = eShop.Basket.API.Model.BasketItem;

namespace eShop.Basket.UnitTests;

[TestClass]
public class BasketServiceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task GetBasketReturnsEmptyForNoUser()
    {
        var mockRepository = Substitute.For<IBasketRepository>();
        var service = new BasketService(mockRepository, NullLogger<BasketService>.Instance);
        var serverCallContext = TestServerCallContext.Create(cancellationToken: TestContext.CancellationToken);
        serverCallContext.SetUserState("__HttpContext", new DefaultHttpContext());

        var response = await service.GetBasket(new GetBasketRequest(), serverCallContext);

        Assert.IsInstanceOfType<CustomerBasketResponse>(response);
        Assert.IsEmpty(response.Items);
    }

    [TestMethod]
    public async Task GetBasketReturnsItemsForValidUserId()
    {
        var mockRepository = Substitute.For<IBasketRepository>();
        List<BasketItem> items = [new BasketItem { Id = "some-id" }];
        mockRepository.GetBasketAsync("1").Returns(Task.FromResult(new CustomerBasket { BuyerId = "1", Items = items }));
        var service = new BasketService(mockRepository, NullLogger<BasketService>.Instance);
        var serverCallContext = TestServerCallContext.Create(cancellationToken: TestContext.CancellationToken);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "1")]));
        serverCallContext.SetUserState("__HttpContext", httpContext);

        var response = await service.GetBasket(new GetBasketRequest(), serverCallContext);

        Assert.IsInstanceOfType<CustomerBasketResponse>(response);
        Assert.HasCount(1, response.Items);
    }

    [TestMethod]
    public async Task GetBasketReturnsEmptyForInvalidUserId()
    {
        var mockRepository = Substitute.For<IBasketRepository>();
        List<BasketItem> items = [new BasketItem { Id = "some-id" }];
        mockRepository.GetBasketAsync("1").Returns(Task.FromResult(new CustomerBasket { BuyerId = "1", Items = items }));
        var service = new BasketService(mockRepository, NullLogger<BasketService>.Instance);
        var serverCallContext = TestServerCallContext.Create(cancellationToken: TestContext.CancellationToken);
        var httpContext = new DefaultHttpContext();
        serverCallContext.SetUserState("__HttpContext", httpContext);

        var response = await service.GetBasket(new GetBasketRequest(), serverCallContext);

        Assert.IsInstanceOfType<CustomerBasketResponse>(response);
        Assert.IsEmpty(response.Items);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(int.MinValue)]
    public async Task UpdateBasketRejectsNonPositiveQuantities(int quantity)
    {
        var repository = Substitute.For<IBasketRepository>();
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);
        var request = new UpdateBasketRequest();
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 10, Quantity = quantity });

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            service.UpdateBasket(request, CreateContext("buyer-1")));

        Assert.AreEqual(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.IsEmpty(repository.ReceivedCalls());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task UpdateBasketRejectsMixedQuantitiesWithoutRepositoryCalls(int quantity)
    {
        var repository = Substitute.For<IBasketRepository>();
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);
        var request = new UpdateBasketRequest();
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 10, Quantity = 2 });
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 20, Quantity = quantity });

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            service.UpdateBasket(request, CreateContext("buyer-1")));

        Assert.AreEqual(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.IsEmpty(repository.ReceivedCalls());
    }

    [TestMethod]
    [DataRow(7)]
    [DataRow(int.MaxValue)]
    public async Task UpdateBasketPersistsItemsForAuthenticatedUser(int quantity)
    {
        var repository = Substitute.For<IBasketRepository>();
        var repositoryBasket = new CustomerBasket("buyer-1")
        {
            Items = [new BasketItem { ProductId = 99, Quantity = 4 }]
        };
        CustomerBasket persistedBasket = null!;
        repository.UpdateBasketAsync(Arg.Any<CustomerBasket>())
            .Returns(call =>
            {
                persistedBasket = call.Arg<CustomerBasket>();
                return repositoryBasket;
            });
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);
        var context = CreateContext("buyer-1");
        var request = new UpdateBasketRequest();
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 10, Quantity = 1 });
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 20, Quantity = quantity });
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 10, Quantity = 1 });

        var response = await service.UpdateBasket(request, context);

        await repository.Received(1).UpdateBasketAsync(Arg.Any<CustomerBasket>());
        Assert.HasCount(1, repository.ReceivedCalls());
        Assert.AreEqual("buyer-1", persistedBasket.BuyerId);
        CollectionAssert.AreEqual(
            new[] { (10, 1), (20, quantity), (10, 1) },
            persistedBasket.Items.Select(item => (item.ProductId, item.Quantity)).ToArray());
        CollectionAssert.AreEqual(
            new[] { (99, 4) },
            response.Items.Select(item => (item.ProductId, item.Quantity)).ToArray());
    }

    [TestMethod]
    public async Task UpdateBasketPreservesEmptyBasket()
    {
        var repository = Substitute.For<IBasketRepository>();
        repository.UpdateBasketAsync(Arg.Any<CustomerBasket>())
            .Returns(new CustomerBasket("buyer-1"));
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);

        var response = await service.UpdateBasket(new UpdateBasketRequest(), CreateContext("buyer-1"));

        await repository.Received(1).UpdateBasketAsync(Arg.Is<CustomerBasket>(basket =>
            basket.BuyerId == "buyer-1" && basket.Items.Count == 0));
        Assert.HasCount(1, repository.ReceivedCalls());
        Assert.IsEmpty(response.Items);
    }

    [TestMethod]
    [DataRow(null, 1)]
    [DataRow(null, 0)]
    [DataRow("", 1)]
    [DataRow("", 0)]
    public async Task UpdateBasketRejectsAnonymousUser(string userId, int quantity)
    {
        var repository = Substitute.For<IBasketRepository>();
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);
        var request = new UpdateBasketRequest();
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 10, Quantity = quantity });

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            service.UpdateBasket(request, CreateContext(userId!)));

        Assert.AreEqual(StatusCode.Unauthenticated, exception.StatusCode);
        Assert.IsEmpty(repository.ReceivedCalls());
    }

    [TestMethod]
    public async Task UpdateBasketReturnsNotFoundWhenRepositoryCannotPersist()
    {
        var repository = Substitute.For<IBasketRepository>();
        repository.UpdateBasketAsync(Arg.Any<CustomerBasket>())
            .Returns(Task.FromResult<CustomerBasket>(null!));
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            service.UpdateBasket(new UpdateBasketRequest(), CreateContext("missing")));

        Assert.AreEqual(StatusCode.NotFound, exception.StatusCode);
    }

    [TestMethod]
    public async Task DeleteBasketRemovesAuthenticatedUsersBasket()
    {
        var repository = Substitute.For<IBasketRepository>();
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);

        await service.DeleteBasket(new DeleteBasketRequest(), CreateContext("buyer-1"));

        await repository.Received(1).DeleteBasketAsync("buyer-1");
    }

    [TestMethod]
    public async Task OrderStartedEventRemovesUsersBasket()
    {
        var repository = Substitute.For<IBasketRepository>();
        var handler = new OrderStartedIntegrationEventHandler(
            repository,
            NullLogger<OrderStartedIntegrationEventHandler>.Instance);

        await handler.Handle(new OrderStartedIntegrationEvent("buyer-1"));

        await repository.Received(1).DeleteBasketAsync("buyer-1");
    }

    private TestServerCallContext CreateContext(string userId)
    {
        var context = TestServerCallContext.Create(cancellationToken: TestContext.CancellationToken);
        var httpContext = new DefaultHttpContext();
        if (userId is not null)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)]));
        }
        context.SetUserState("__HttpContext", httpContext);
        return context;
    }
}
