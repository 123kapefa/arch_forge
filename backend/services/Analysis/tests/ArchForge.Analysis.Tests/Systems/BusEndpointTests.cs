using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

public class BusEndpointTests
{
    [Theory]
    // первый сегмент — известная шина, последний — host/broker-подобное поле
    [InlineData("RabbitMQ__Host", true)]
    [InlineData("RabbitMQ__HostName", true)]
    [InlineData("Kafka__BootstrapServers", true)]
    [InlineData("Kafka__Brokers", true)]
    [InlineData("MassTransit__RabbitMq__Host", true)]
    [InlineData("Nats__Url", true)]
    [InlineData("RabbitMQ__Uri", true)]
    // не шина в первом сегменте
    [InlineData("Redis__Host", false)]
    [InlineData("Postgres__Host", false)]
    [InlineData("ConnectionStrings__Bus", false)]
    // шина в первом сегменте, но НЕ host-подобное поле → не создаём uses-bus
    [InlineData("RabbitMQ__Password", false)]
    [InlineData("RabbitMQ__Port", false)]
    [InlineData("RabbitMQ__VirtualHost", false)]
    public void IsHostKey_matches_only_bus_host_keys(string key, bool expected) =>
        Assert.Equal(expected, BusEndpoint.IsHostKey(key));

    [Theory]
    [InlineData("rabbitmq", "rabbitmq")]                          // голый хост
    [InlineData("kafka:9092", "kafka")]                           // хост:порт
    [InlineData("kafka1:9092,kafka2:9092", "kafka1")]             // несколько брокеров → первый
    [InlineData("amqp://guest:guest@rabbitmq:5672/vhost", "rabbitmq")] // схема + userinfo + порт + vhost
    [InlineData("rabbitmq.messaging.svc.cluster.local", "rabbitmq")]   // DNS-хвост → первый сегмент
    [InlineData("mybroker.servicebus.windows.net", "mybroker")]  // облачный брокер
    // отбрасываемое → null
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("${RABBIT_HOST}", null)]                          // обязательная переменная без дефолта
    [InlineData(":5672", null)]                                   // только порт
    [InlineData("10.0.0.5:5672", null)]                          // IPv4-литерал
    [InlineData("[::1]:5672", null)]                              // IPv6-литерал
    [InlineData("localhost:5672", null)]                          // локалхост — не сервисный узел
    public void Host_extracts_broker_host(string value, string? expected) =>
        Assert.Equal(expected, BusEndpoint.Host(value));
}
