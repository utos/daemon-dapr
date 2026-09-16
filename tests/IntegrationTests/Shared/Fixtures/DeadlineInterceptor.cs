using Grpc.Core;
using Grpc.Core.Interceptors;

namespace IntegrationTests.Shared.Fixtures;

/// <summary>
/// Gives every gRPC call a deadline it did not set for itself.
///
/// <para>
/// Without one, a wedged daemon is indistinguishable from a hung test run: the call never
/// returns, so the polling loops that wrap it never get back round to checking their own
/// timeout, xUnit reports nothing, and the whole run dies to the CI job timeout with no test
/// named. A deadline turns that into <c>DEADLINE_EXCEEDED</c> raised inside the test that caused
/// it — a failure with a name and a stack, which is the difference between a diagnosable bug and
/// an afternoon with <c>--blame-hang</c>.
/// </para>
///
/// <para>
/// A call that sets its own deadline keeps it. Server streams get the longer allowance because a
/// watch legitimately waits out an entire run; they are bounded more tightly in practice by the
/// cancellation tokens the read helpers pass to <c>MoveNext</c>, and this is only the backstop
/// for the case where the stream never produces anything at all.
/// </para>
///
/// <para>
/// Client and duplex streaming are not overridden: <c>daemon/v1</c> declares no such RPC, and an
/// unimplemented override that silently skips the deadline would be worse than the compile error
/// adding one would cause.
/// </para>
/// </summary>
internal sealed class DeadlineInterceptor(TimeSpan unary, TimeSpan streaming) : Interceptor
{
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithDeadline(context, unary));

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithDeadline(context, unary));

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithDeadline(context, streaming));

    private static ClientInterceptorContext<TRequest, TResponse> WithDeadline<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, TimeSpan deadline)
        where TRequest : class
        where TResponse : class =>
        context.Options.Deadline is not null
            ? context
            : new ClientInterceptorContext<TRequest, TResponse>(
                context.Method,
                context.Host,
                context.Options.WithDeadline(DateTime.UtcNow + deadline));
}
