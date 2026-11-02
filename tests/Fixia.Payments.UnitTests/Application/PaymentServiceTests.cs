using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Fixia.Payments.UnitTests.Application;

public class PaymentServiceTests
{
    private readonly Mock<IPaymentRepository> _paymentRepositoryMock;
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<IPaymentGateway> _paymentGatewayMock;
    private readonly Mock<IBusinessEventOutbox> _outboxMock;
    private readonly Mock<ILogger<PaymentService>> _loggerMock;
    private readonly List<BusinessEvent> _enqueued = new();
    private readonly PaymentService _sut;

    public PaymentServiceTests()
    {
        _paymentRepositoryMock = new Mock<IPaymentRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _paymentGatewayMock = new Mock<IPaymentGateway>();
        _outboxMock = new Mock<IBusinessEventOutbox>();
        _loggerMock = new Mock<ILogger<PaymentService>>();

        _outboxMock.Setup(o => o.EnqueueAsync(It.IsAny<BusinessEvent>(), It.IsAny<CancellationToken>()))
            .Callback<BusinessEvent, CancellationToken>((e, _) => _enqueued.Add(e))
            .Returns(Task.CompletedTask);

        _sut = new PaymentService(
            _paymentRepositoryMock.Object,
            _unitOfWorkMock.Object,
            _paymentGatewayMock.Object,
            _outboxMock.Object,
            _loggerMock.Object
        );
    }

    private static Requester Client(Guid id) => new(id, new[] { Requester.RoleClient });
    private static Requester Professional(Guid id) => new(id, new[] { Requester.RoleProfessional });
    private static Requester Admin() => new(Guid.NewGuid(), new[] { Requester.RoleAdmin });

    private void GivenPayment(Payment payment)
    {
        _paymentRepositoryMock.Setup(r => r.GetByIdAsync(payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);
        _paymentRepositoryMock.Setup(r => r.GetByServiceRequestIdAsync(payment.ServiceRequestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);
    }

    [Fact]
    public async Task CreatePaymentAsync_WhenValidRequest_ShouldSaveAndReturnResponse()
    {
        // Arrange
        var request = new CreatePaymentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            75000m,
            "COP",
            PaymentMethod.CreditCard
        );

        _paymentRepositoryMock.Setup(r => r.GetByServiceRequestIdAsync(request.ServiceRequestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        // Act
        var result = await _sut.CreatePaymentAsync(request);

        // Assert
        result.Created.Should().BeTrue();
        result.Payment.Amount.Should().Be(75000m);
        result.Payment.Status.Should().Be(PaymentStatus.Pending);

        _paymentRepositoryMock.Verify(r => r.AddAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _enqueued.Should().ContainSingle(e => e.EventType == PaymentEvents.Created);
    }

    [Fact]
    public async Task CreatePaymentAsync_WhenSameRequestIsRetried_ShouldReturnExistingWithoutDuplicating()
    {
        // Arrange
        var existing = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 75000m, "COP", PaymentMethod.CreditCard);
        GivenPayment(existing);
        var retry = new CreatePaymentRequest(existing.ServiceRequestId, existing.ClientId, existing.ProviderId,
            75000m, "cop", PaymentMethod.CreditCard);

        // Act
        var result = await _sut.CreatePaymentAsync(retry);

        // Assert
        result.Created.Should().BeFalse();
        result.Payment.Id.Should().Be(existing.Id);
        _paymentRepositoryMock.Verify(r => r.AddAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatePaymentAsync_WhenServiceRequestHasPaymentWithOtherData_ShouldThrowConflict()
    {
        // Arrange
        var existing = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 75000m, "COP", PaymentMethod.CreditCard);
        GivenPayment(existing);
        var different = new CreatePaymentRequest(existing.ServiceRequestId, existing.ClientId, existing.ProviderId,
            90000m, "COP", PaymentMethod.CreditCard);

        // Act
        var action = () => _sut.CreatePaymentAsync(different);

        // Assert
        await action.Should().ThrowAsync<PaymentConflictException>();
        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessPaymentAsync_WhenGatewaySucceeds_ShouldCompletePayment()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m, "COP", PaymentMethod.CreditCard);
        GivenPayment(payment);

        _paymentGatewayMock.Setup(g => g.ProcessChargeAsync(
                payment.Id, payment.Amount, payment.Currency, payment.Method, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayResult(true, "GW-APPROVED-123"));

        // Act
        var result = await _sut.ProcessPaymentAsync(payment.Id, Client(payment.ClientId));

        // Assert
        result.Status.Should().Be(PaymentStatus.Completed);
        result.TransactionReference.Should().Be("GW-APPROVED-123");

        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        var completed = _enqueued.Should().ContainSingle().Subject;
        completed.EventType.Should().Be(PaymentEvents.Completed);
        completed.Actor.Should().Be(new EventActor(payment.ClientId, "CLIENT"));
        completed.ServiceRequestId.Should().Be(payment.ServiceRequestId);
    }

    [Fact]
    public async Task ProcessPaymentAsync_WhenGatewayFails_ShouldMarkFailedAndEnqueueFailedEvent()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m, "COP", PaymentMethod.CreditCard);
        GivenPayment(payment);
        _paymentGatewayMock.Setup(g => g.ProcessChargeAsync(
                payment.Id, payment.Amount, payment.Currency, payment.Method, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayResult(false, string.Empty, "Fondos insuficientes"));

        // Act
        var result = await _sut.ProcessPaymentAsync(payment.Id, Client(payment.ClientId));

        // Assert
        result.Status.Should().Be(PaymentStatus.Failed);
        _enqueued.Should().ContainSingle(e => e.EventType == PaymentEvents.Failed);
    }

    [Fact]
    public async Task ProcessPaymentAsync_WhenPaymentNotFound_ShouldThrowPaymentNotFoundException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        _paymentRepositoryMock.Setup(r => r.GetByIdAsync(nonExistentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        // Act
        var action = () => _sut.ProcessPaymentAsync(nonExistentId, Client(Guid.NewGuid()));

        // Assert
        await action.Should().ThrowAsync<PaymentNotFoundException>();
    }

    [Fact]
    public async Task ProcessPaymentAsync_WhenRequesterIsTheProvider_ShouldBeDenied()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m, "COP", PaymentMethod.CreditCard);
        GivenPayment(payment);

        // Act
        var action = () => _sut.ProcessPaymentAsync(payment.Id, Professional(payment.ProviderId));

        // Assert
        await action.Should().ThrowAsync<PaymentAccessDeniedException>();
        payment.Status.Should().Be(PaymentStatus.Pending);
        _paymentGatewayMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessPaymentAsync_WhenRequesterDoesNotParticipate_ShouldLookLikeNotFound()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m, "COP", PaymentMethod.CreditCard);
        GivenPayment(payment);

        // Act
        var action = () => _sut.ProcessPaymentAsync(payment.Id, Client(Guid.NewGuid()));

        // Assert
        await action.Should().ThrowAsync<PaymentNotFoundException>();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldOnlyShowPaymentToParticipantsAndAdmin()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m, "COP", PaymentMethod.CreditCard);
        GivenPayment(payment);

        // Act & Assert
        (await _sut.GetByIdAsync(payment.Id, Client(payment.ClientId))).Id.Should().Be(payment.Id);
        (await _sut.GetByIdAsync(payment.Id, Professional(payment.ProviderId))).Id.Should().Be(payment.Id);
        (await _sut.GetByIdAsync(payment.Id, Admin())).Id.Should().Be(payment.Id);
        await FluentActions.Invoking(() => _sut.GetByIdAsync(payment.Id, Client(Guid.NewGuid())))
            .Should().ThrowAsync<PaymentNotFoundException>();
    }

    [Fact]
    public async Task GetByServiceRequestIdAsync_WhenRequesterDoesNotParticipate_ShouldThrowNotFound()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m, "COP", PaymentMethod.CreditCard);
        GivenPayment(payment);

        // Act
        var action = () => _sut.GetByServiceRequestIdAsync(payment.ServiceRequestId, Professional(Guid.NewGuid()));

        // Assert
        await action.Should().ThrowAsync<PaymentNotFoundException>();
    }
}
