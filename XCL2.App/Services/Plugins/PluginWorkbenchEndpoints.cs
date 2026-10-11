using System.Reflection;
using System.Text.Json;

namespace XCL2.App.Services.Plugins;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class WorkbenchEndpointAttribute(string name) : Attribute { internal string Name { get; } = name; }

/// <summary>Only explicitly attributed methods on these four services are available to EXE plugins.</summary>
internal static class PluginWorkbenchEndpoints
{
    internal static async Task<object?> InvokeAsync(PluginContext context, string route, JsonElement args, CancellationToken ct)
    {
        object[] services = { context.Projects, context.Tasks, context.Changes, context.Queries };
        foreach (var service in services)
        foreach (var method in service.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<WorkbenchEndpointAttribute>()?.Name != route) continue;
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("params 必须是对象。");
            var values = method.GetParameters().Select(parameter => parameter.ParameterType == typeof(CancellationToken) ? (object)ct :
                args.TryGetProperty(parameter.Name!, out var value) ? value.Deserialize(parameter.ParameterType, PluginPipeSession.Json) :
                parameter.HasDefaultValue ? parameter.DefaultValue : throw new ArgumentException("缺少参数：" + parameter.Name)).ToArray();
            object? result;
            try { result = method.Invoke(service, values); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            if (result is Task task) { await task.ConfigureAwait(false); return task.GetType().GetProperty("Result")?.GetValue(task); }
            return result;
        }
        throw new PluginOperationException("UnknownMethod", "未找到工作台接口。");
    }
}
