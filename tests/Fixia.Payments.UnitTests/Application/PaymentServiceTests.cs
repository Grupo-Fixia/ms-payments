using Fixia.Payments.Application.Common.Interfaces;
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
    private readonly Mock<IEventPublisher> _eventPublisherMock;
    private readonly Mock<ILogger<PaymentService>> _loggerMock;
    private readonly PaymentService _sut;

    public PaymentServiceTests()
    {
        _paymentRepositoryMock = new Mock<IPaymentRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _paymentGatewayMock = new Mock<IPaymentGateway>();
        _eventPublisherMock = new Mock<IEventPublisher>();
        _loggerMock = new Mock<ILogger<PaymentService>>();

        _sut = new PaymentService(
            _paymentRepositoryMock.Object,
            _unitOfWorkMock.Object,
            _paymentGatewayMock.Object,
            _eventPublisherMock.Object,
            _loggerMock.Object
        );
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
        result.Should().NotBeNull();
        result.Amount.Should().Be(75000m);
        result.Status.Should().Be(PaymentStatus.Pending);

        _paymentRepositoryMock.Verify(r => r.AddAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _eventPublisherMock.Verify(e => e.PublishAsync("fixia.payments.events", "pago.creado", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPaymentAsync_WhenGatewaySucceeds_ShouldCompletePayment()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m, "COP", PaymentMethod.CreditCard);

        _paymentRepositoryMock.Setup(r => r.GetByIdAsync(payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);

        _paymentGatewayMock.Setup(g => g.ProcessChargeAsync(
                payment.Id, payment.Amount, payment.Currency, payment.Method, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayResult(true, "GW-APPROVED-123"));

        // Act
        var result = await _sut.ProcessPaymentAsync(payment.Id);

        // Assert
        result.Status.Should().Be(PaymentStatus.Completed);
        result.TransactionReference.Should().Be("GW-APPROVED-123");

        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _eventPublisherMock.Verify(e => e.PublishAsync("fixia.payments.events", "pago.completado", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPaymentAsync_WhenPaymentNotFound_ShouldThrowPaymentNotFoundException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        _paymentRepositoryMock.Setup(r => r.GetByIdAsync(nonExistentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        // Act
        var action = () => _sut.ProcessPaymentAsync(nonExistentId);

        // Assert
        await action.Should().ThrowAsync<PaymentNotFoundException>();
    }

    [Fact]
    public async Task RefundPaymentAsync_WhenPaymentIsCompleted_ShouldRefundSuccessfully()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m, "COP", PaymentMethod.CreditCard);
        payment.MarkAsCompleted("TX-SUCCESS");

        _paymentRepositoryMock.Setup(r => r.GetByIdAsync(payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);

        _paymentGatewayMock.Setup(g => g.ProcessRefundAsync(payment.Id, "TX-SUCCESS", payment.Amount, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayResult(true, "REF-APPROVED-999"));

        // Act
        var result = await _sut.RefundPaymentAsync(payment.Id, "Cliente canceló servicio");

        // Assert
        result.Status.Should().Be(PaymentStatus.Refunded);
        _eventPublisherMock.Verify(e => e.PublishAsync("fixia.payments.events", "pago.reembolsado", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

