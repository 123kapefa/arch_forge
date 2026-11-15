using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Направление pub/sub из кода (#61): кто публикует, кто потребляет. Синтаксический разбор; сомнительное — низкая уверенность.
public class RoslynBusSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_bus_" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ } }

    private IReadOnlyList<BusSite> Scan(params (string Rel, string Code)[] files)
    {
        foreach (var (rel, code) in files)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, code);
        }
        return new RoslynBusSource().Read(_root);
    }

    [Fact]
    public void MassTransit_consumer_marker_is_subscribe_with_high_confidence()
    {
        var s = Scan(("C.cs", "using MassTransit;\nclass OnCreated : IConsumer<OrderCreated> { }"));

        var site = Assert.Single(s);
        Assert.Equal((BusDirection.Subscribe, "OrderCreated", BusBasis.Marker, 2), (site.Direction, site.Name, site.Basis, site.Line));
        Assert.True(site.Confidence >= 0.8);
    }

    [Fact]
    public void Publish_generic_and_new_expression_give_event_type()
    {
        var s = Scan(("P.cs", """
            using MassTransit;
            class P(IPublishEndpoint bus) {
                async Task A() { await bus.Publish<OrderCreated>(new { Id = 1 }); }
                async Task B() { await bus.Publish(new OrderPaid(1)); }
            }
            """));

        Assert.Equal(2, s.Count);
        Assert.Contains(s, x => x is { Direction: BusDirection.Publish, Name: "OrderCreated", Line: 3 });
        Assert.Contains(s, x => x is { Direction: BusDirection.Publish, Name: "OrderPaid", Line: 4 });
    }

    [Fact]
    public void Publish_of_a_variable_is_publish_with_unresolved_name_and_low_confidence()
    {
        var s = Scan(("P.cs", "using MassTransit;\nclass P(IPublishEndpoint bus) { Task A(object e) => bus.Publish(e); }"));

        var site = Assert.Single(s);
        Assert.Equal((BusDirection.Publish, "", BusBasis.Unresolved), (site.Direction, site.Name, site.Basis));
        Assert.True(site.Confidence < 0.6);
    }

    [Fact]
    public void MediatR_publish_is_in_process_and_not_a_bus_publication()
    {
        var s = Scan(("P.cs", "using MediatR;\nclass P(IMediator mediator) { Task A() => mediator.Publish(new OrderCreated()); }"));
        Assert.Empty(s);
    }

    [Fact]
    public void Mediator_receiver_is_ignored_even_when_a_bus_library_is_imported()
    {
        var s = Scan(("P.cs", "using MassTransit;\nusing MediatR;\nclass P(IMediator _mediator) { Task A() => _mediator.Publish(new Local()); }"));
        Assert.DoesNotContain(s, x => x.Direction == BusDirection.Publish);
    }

    [Fact]
    public void Publish_without_any_bus_library_import_is_not_a_publication()
    {
        // свой метод Publish (кеш, события домена) — не шина
        var s = Scan(("P.cs", "class P(Dispatcher d) { void A() => d.Publish(new Local()); }"));
        Assert.Empty(s);
    }

    [Fact]
    public void NServiceBus_handler_marker_and_publish()
    {
        var s = Scan(("H.cs", "using NServiceBus;\nclass H : IHandleMessages<PaymentTaken> { }\nclass P(IMessageSession s) { Task A() => s.Publish(new PaymentTaken()); }"));

        Assert.Contains(s, x => x is { Direction: BusDirection.Subscribe, Name: "PaymentTaken" });
        Assert.Contains(s, x => x is { Direction: BusDirection.Publish, Name: "PaymentTaken" });
    }

    [Fact]
    public void Wolverine_handler_by_naming_convention_is_subscribe_with_low_confidence()
    {
        var s = Scan(("H.cs", "using Wolverine;\npublic class OrderHandler { public void Handle(OrderCreated e) { } }"));

        var site = Assert.Single(s);
        Assert.Equal((BusDirection.Subscribe, "OrderCreated", BusBasis.Convention), (site.Direction, site.Name, site.Basis));
        Assert.True(site.Confidence < 0.6);
    }

    [Fact]
    public void Convention_handler_without_wolverine_import_is_ignored()
    {
        var s = Scan(("H.cs", "public class OrderHandler { public void Handle(OrderCreated e) { } }"));
        Assert.Empty(s);
    }

    [Fact]
    public void Kafka_string_topics_publish_and_subscribe()
    {
        var s = Scan(("K.cs", """
            using Confluent.Kafka;
            class K(IProducer<string,string> p, IConsumer<string,string> c) {
                void A() { c.Subscribe("orders"); }
                Task B() => p.ProduceAsync("orders", new Message<string,string>());
            }
            """));

        Assert.Contains(s, x => x is { Direction: BusDirection.Subscribe, Name: "orders", Basis: BusBasis.String, Line: 3 });
        Assert.Contains(s, x => x is { Direction: BusDirection.Publish, Name: "orders", Basis: BusBasis.String, Line: 4 });
    }

    [Fact]
    public void RabbitMq_basic_publish_and_consume_give_direction()
    {
        var s = Scan(("R.cs", """
            using RabbitMQ.Client;
            class R(IModel ch) {
                void A() { ch.BasicPublish(exchange: "", routingKey: "q1", body: null); }
                void B() { ch.BasicConsume(queue: "q1", autoAck: true, consumer: null); }
            }
            """));

        Assert.Contains(s, x => x is { Direction: BusDirection.Publish, Name: "q1" });
        Assert.Contains(s, x => x is { Direction: BusDirection.Subscribe, Name: "q1" });
    }

    [Fact]
    public void CAP_subscribe_attribute_is_subscribe_by_topic()
    {
        var s = Scan(("C.cs", "using DotNetCore.CAP;\nclass C { [CapSubscribe(\"order.created\")] public void On(object m) { } }"));

        Assert.Contains(s, x => x is { Direction: BusDirection.Subscribe, Name: "order.created" });
    }

    [Fact]
    public void Raw_client_file_with_unrecognized_usage_is_unknown_direction()
    {
        // клиент шины подключён, но направление не распознано — шину по такому сервису нельзя считать полной
        var s = Scan(("R.cs", "using RabbitMQ.Client;\nclass R(IConnection c) { void A() { var x = c.CreateModel(); Wire(x); } }"));

        var site = Assert.Single(s);
        Assert.Equal(BusDirection.Unknown, site.Direction);
    }

    [Fact]
    public void Framework_config_only_does_not_create_unknown()
    {
        // AddMassTransit(...) в composition root — это настройка, а не направление; потребители видны по маркерам в других файлах
        var s = Scan(("S.cs", "using MassTransit;\nclass S { void A(IServiceCollection c) { c.AddMassTransit(x => { }); } }"),
                     ("C.cs", "using MassTransit;\nclass C : IConsumer<E> { }"));

        Assert.All(s, x => Assert.NotEqual(BusDirection.Unknown, x.Direction));
    }

    [Fact]
    public void Long_call_chain_does_not_blow_up_and_site_count_is_capped()
    {
        var chain = string.Concat(Enumerable.Repeat(".Select(x => x)", 3000));
        var publishes = string.Concat(Enumerable.Range(0, 2000).Select(i => $"    void M{i}(IPublishEndpoint b) => b.Publish(new E{i}());\n"));
        var s = Scan(("Big.cs", $"using MassTransit;\nclass Big {{ void Chain(IEnumerable<int> q) => q{chain}.ToList();\n{publishes}}}"));

        Assert.InRange(s.Count, 1, 501);                                  // 500 мест + признак усечения
        Assert.Contains(s, x => x is { Direction: BusDirection.Unknown, Basis: BusBasis.Truncated });
    }

    [Fact]
    public void Cancellation_is_honored()
    {
        Scan(("C.cs", "using MassTransit;\nclass C : IConsumer<E> { }"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new RoslynBusSource().Read(_root, cts.Token));
    }

    [Fact]
    public void Site_cap_adds_truncated_unknown_so_bus_is_not_considered_complete()
    {
        var code = "using MassTransit;\nclass P(IPublishEndpoint b) {\n" + string.Concat(Enumerable.Range(0, 10).Select(i => $"  void M{i}() => b.Publish(new E{i}());\n")) + "}";
        Scan(("P.cs", code));

        var s = new RoslynBusSource(maxFiles: 100, maxSites: 3).Read(_root);

        Assert.Contains(s, x => x is { Direction: BusDirection.Unknown, Basis: BusBasis.Truncated });
    }

    [Fact]
    public void File_cap_adds_truncated_unknown()
    {
        Scan(("A.cs", "using MassTransit;\nclass A : IConsumer<E1> { }"), ("B.cs", "using MassTransit;\nclass B : IConsumer<E2> { }"), ("C.cs", "using MassTransit;\nclass C : IConsumer<E3> { }"));

        var s = new RoslynBusSource(maxFiles: 2, maxSites: 500).Read(_root);

        Assert.Contains(s, x => x is { Direction: BusDirection.Unknown, Basis: BusBasis.Truncated });
        Assert.DoesNotContain(new RoslynBusSource().Read(_root), x => x.Basis == BusBasis.Truncated);   // без капа — признака нет
    }

    [Theory]
    [InlineData("class S : MassTransitStateMachine<State> { }")]
    [InlineData("class S(IBus b) { Task A() => b.Send(new Cmd()); }")]
    [InlineData("class S(IRequestClient<Q> c) { Task A() => c.GetResponse<R>(new Q()); }")]
    [InlineData("class S { Task A(IConsumer<K,V> c) => c.ConsumeAsync(); }")]
    public void Unrecognized_consumption_is_unknown_even_when_other_sites_exist(string extra)
    {
        var s = Scan(("S.cs", "using MassTransit;\nclass C : IConsumer<E> { }\n" + extra));

        Assert.Contains(s, x => x.Direction == BusDirection.Subscribe);
        Assert.Contains(s, x => x.Direction == BusDirection.Unknown);
    }

    [Fact]
    public void Kafka_consume_without_subscribe_is_unknown_but_with_subscribe_is_not()
    {
        var bare = Scan(("K.cs", "using Confluent.Kafka;\nclass K(IProducer<string,string> p, IConsumer<string,string> c) { void A() { c.Assign(null); p.Produce(\"t\", null); } }"));
        Assert.Contains(bare, x => x.Direction == BusDirection.Unknown);

        File.Delete(Path.Combine(_root, "K.cs"));
        var ok = Scan(("K2.cs", "using Confluent.Kafka;\nclass K(IConsumer<string,string> c) { void A() { c.Subscribe(\"t\"); c.Consume(); } }"));
        Assert.DoesNotContain(ok, x => x.Direction == BusDirection.Unknown);
    }

    [Fact]
    public void Eventing_consumer_without_basic_consume_is_unknown()
    {
        var s = Scan(("R.cs", "using RabbitMQ.Client;\nusing RabbitMQ.Client.Events;\nclass R(IModel ch) { void A() { var c = new EventingBasicConsumer(ch); ch.BasicPublish(\"\", \"q\", null, null); } }"));

        Assert.Contains(s, x => x.Direction == BusDirection.Unknown);
    }

    [Fact]
    public void MediatR_IPublisher_is_in_process_even_with_bus_import()
    {
        var s = Scan(("P.cs", "using MassTransit;\nusing MediatR;\nclass P(IPublisher _publisher) { Task A() => _publisher.Publish(new Local()); }\nclass C : IConsumer<E> { }"));

        Assert.DoesNotContain(s, x => x.Direction is BusDirection.Publish or BusDirection.Unknown);
    }

    [Fact]
    public void Dispatcher_or_custom_wrapper_publish_is_unknown_not_a_publication()
    {
        var s = Scan(("P.cs", "using MassTransit;\nclass P(IEventBus eventBus, IDomainEventDispatcher _dispatcher) { void A() { eventBus.Publish(new E()); _dispatcher.Publish(new E()); } }\nclass C : IConsumer<E> { }"));

        Assert.DoesNotContain(s, x => x.Direction == BusDirection.Publish);
        Assert.Contains(s, x => x.Direction == BusDirection.Unknown);
    }

    [Fact]
    public void Rebus_subscribe_generic_is_subscription()
    {
        var s = Scan(("R.cs", "using Rebus.Bus;\nclass R(IBus bus) { Task A() => bus.Subscribe<OrderCreated>(); }"));

        Assert.Contains(s, x => x is { Direction: BusDirection.Subscribe, Name: "OrderCreated" });
    }

    [Fact]
    public void Rx_subscribe_with_method_group_or_lambda_is_not_kafka()
    {
        var s = Scan(("K.cs", "using Confluent.Kafka;\nclass K(IObservable<int> o, IProducer<string,string> p) { void OnNext(int x) { } void A() { o.Subscribe(OnNext); o.Subscribe(x => { }); p.Produce(\"t\", null); } }"));

        Assert.DoesNotContain(s, x => x.Direction == BusDirection.Subscribe);
    }

    [Theory]
    [InlineData("Host=db;Password=secret;")]
    [InlineData("amqp://user:pass@host/vhost")]
    [InlineData("topic with spaces")]
    public void Literals_that_look_like_connection_strings_are_not_exposed(string literal)
    {
        var s = Scan(("K.cs", $"using Confluent.Kafka;\nclass K(IConsumer<string,string> c) {{ void A() {{ c.Subscribe(\"{literal}\"); }} }}"));

        var site = Assert.Single(s);
        Assert.Equal((BusDirection.Subscribe, "", BusBasis.Unresolved), (site.Direction, site.Name, site.Basis));
    }

    [Fact]
    public void Global_using_and_fully_qualified_names_are_not_seen()
    {
        // зафиксированное ограничение: файл без собственного using не считается подключающим библиотеку шины
        var s = Scan(("G.cs", "global using MassTransit;"), ("P.cs", "class P(IPublishEndpoint b) { Task A() => b.Publish(new E()); }"),
                     ("Q.cs", "class Q(MassTransit.IPublishEndpoint b) { Task A() => b.Publish(new E()); }"));

        Assert.Empty(s);
    }
}
