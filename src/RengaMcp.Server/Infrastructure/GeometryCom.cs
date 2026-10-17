using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using RengaMcp.Models;

namespace RengaMcp.Infrastructure;

// Renga's IDispatch implementation omits members with record arguments/returns.
// Resolve the dual interface IID and method slot from the installed type library,
// then use the documented native signatures. No SDK artifacts are redistributed.
internal static class GeometryCom
{
    public static Point3DValue GetPoint3(object target, string member)
    {
        using var method = Resolve(target, member, INVOKEKIND.INVOKE_PROPERTYGET);
        var result = new NativePoint3();
        Marshal.ThrowExceptionForHR(method.As<Point3Out>()(method.Instance, out result));
        return result.Value;
    }

    public static Point2DValue GetPoint2(object target, string member, double? parameter = null)
    {
        using var method = Resolve(target, member, INVOKEKIND.INVOKE_FUNC);
        NativePoint2 result;
        var hr = parameter is double number
            ? method.As<DoublePoint2Out>()(method.Instance, number, out result)
            : method.As<Point2Out>()(method.Instance, out result);
        Marshal.ThrowExceptionForHR(hr);
        return result.Value;
    }

    public static void SetCreationPlacement(object arguments, Placement3DValue placement)
    {
        using var method = Resolve(arguments, "Placement3D", INVOKEKIND.INVOKE_PROPERTYPUT);
        Marshal.ThrowExceptionForHR(method.As<PlacementIn>()(method.Instance, new NativePlacement(placement)));
    }

    public static object CreatePlacement(object math, Placement3DValue placement)
    {
        using var method = Resolve(math, "CreatePlacement3D", INVOKEKIND.INVOKE_FUNC);
        Marshal.ThrowExceptionForHR(method.As<PlacementOut>()(method.Instance,
            new NativePoint3(placement.Origin), new NativePoint3(placement.AxisX), new NativePoint3(placement.AxisZ), out var result));
        return Wrap(result);
    }

    public static object CreateLine2(object math, Point2DValue start, Point2DValue end)
    {
        using var method = Resolve(math, "CreateLineSegment2D", INVOKEKIND.INVOKE_FUNC);
        Marshal.ThrowExceptionForHR(method.As<Line2Out>()(method.Instance, new NativePoint2(start), new NativePoint2(end), out var result));
        return Wrap(result);
    }

    public static object CreateComposite2(object math, IReadOnlyList<object> curves)
    {
        var array = SafeArrayCreateVector(9, 0, (uint)curves.Count); // VT_DISPATCH
        if (array == IntPtr.Zero) throw new InvalidOperationException("SAFEARRAY allocation failed.");
        try
        {
            for (var i = 0; i < curves.Count; i++)
            {
                var pointer = Marshal.GetIDispatchForObject(curves[i]);
                try { var index = i; Marshal.ThrowExceptionForHR(SafeArrayPutElement(array, ref index, pointer)); }
                finally { _ = Marshal.Release(pointer); }
            }
            using var method = Resolve(math, "CreateCompositeCurve2D", INVOKEKIND.INVOKE_FUNC);
            Marshal.ThrowExceptionForHR(method.As<ArrayObjectOut>()(method.Instance, array, out var result));
            return Wrap(result);
        }
        finally { _ = SafeArrayDestroy(array); }
    }

    public static object CreateUndoOperation(object project, object model)
    {
        using var idMethod = Resolve(model, "Id", INVOKEKIND.INVOKE_PROPERTYGET);
        Marshal.ThrowExceptionForHR(idMethod.As<GuidOut>()(idMethod.Instance, out var id));
        using var create = Resolve(project, "CreateOperationWithUndo", INVOKEKIND.INVOKE_FUNC);
        Marshal.ThrowExceptionForHR(create.As<GuidObjectOut>()(create.Instance, id, out var result));
        return Wrap(result);
    }

    public static object CreateScreenshotSettings(object view)
    {
        var service = GetViewInterface(view, "IScreenshotService");
        try { return ComDispatch.Call(service, "CreateSettings") ?? throw new InvalidOperationException("Screenshot settings are unavailable."); }
        finally { ComDispatch.Release(service); }
    }

    public static object MakeScreenshot(object view, object settings)
    {
        var service = GetViewInterface(view, "IScreenshotService");
        try { return ComDispatch.Call(service, "MakeScreenshot", settings) ?? throw new InvalidOperationException("Renga returned no screenshot."); }
        finally { ComDispatch.Release(service); }
    }

    public static object GetViewInterface(object view, string name)
    {
        using var method = Resolve(view, "GetInterfaceByName", INVOKEKIND.INVOKE_FUNC, "IModelView");
        Marshal.ThrowExceptionForHR(method.As<StringObjectOut>()(method.Instance, name, out var result));
        return Wrap(result);
    }

    private static object Wrap(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Renga returned no math object.");
        try { return Marshal.GetObjectForIUnknown(pointer); }
        finally { _ = Marshal.Release(pointer); }
    }

    private static Method Resolve(object target, string name, INVOKEKIND kind, string? interfaceName = null)
    {
        var dispatch = (INativeDispatch)target;
        Marshal.ThrowExceptionForHR(dispatch.GetTypeInfo(0, 0, out var info));
        try
        {
            if (interfaceName is not null)
            {
                info.GetContainingTypeLib(out var library, out _);
                try
                {
                    ITypeInfo? requested = null;
                    for (var index = 0; index < library.GetTypeInfoCount(); index++)
                    {
                        library.GetDocumentation(index, out var candidate, out _, out _, out _);
                        if (string.Equals(interfaceName, candidate, StringComparison.Ordinal))
                        {
                            library.GetTypeInfo(index, out requested);
                            break;
                        }
                    }
                    if (requested is null) throw new NotSupportedException($"Installed Renga does not expose {interfaceName}.");
                    ComDispatch.Release(info); info = requested;
                }
                finally { ComDispatch.Release(library); }
            }
            info.GetTypeAttr(out var attributePtr);
            TYPEATTR attribute;
            try { attribute = Marshal.PtrToStructure<TYPEATTR>(attributePtr); }
            finally { info.ReleaseTypeAttr(attributePtr); }
            // A dual dispatch type has a corresponding native interface type.
            if (attribute.typekind == TYPEKIND.TKIND_DISPATCH)
            {
                info.GetRefTypeOfImplType(-1, out var reference);
                info.GetRefTypeInfo(reference, out var nativeInfo);
                ComDispatch.Release(info);
                info = nativeInfo;
                info.GetTypeAttr(out attributePtr);
                try { attribute = Marshal.PtrToStructure<TYPEATTR>(attributePtr); }
                finally { info.ReleaseTypeAttr(attributePtr); }
            }
            for (var i = 0; i < attribute.cFuncs; i++)
            {
                info.GetFuncDesc(i, out var descriptorPtr);
                try
                {
                    var descriptor = Marshal.PtrToStructure<FUNCDESC>(descriptorPtr);
                    info.GetDocumentation(descriptor.memid, out var member, out _, out _, out _);
                    if (!string.Equals(name, member, StringComparison.Ordinal) || descriptor.invkind != kind) continue;
                    var unknown = Marshal.GetIUnknownForObject(target);
                    try
                    {
                        var iid = attribute.guid;
                        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out var instance));
                        var table = Marshal.ReadIntPtr(instance);
                        return new Method(instance, Marshal.ReadIntPtr(table, descriptor.oVft));
                    }
                    finally { _ = Marshal.Release(unknown); }
                }
                finally { info.ReleaseFuncDesc(descriptorPtr); }
            }
            throw new NotSupportedException($"Installed Renga does not expose native {name} ({kind}).");
        }
        finally { ComDispatch.Release(info); }
    }

    private sealed class Method(IntPtr instance, IntPtr address) : IDisposable
    {
        public IntPtr Instance { get; } = instance;
        public T As<T>() where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(address);
        public void Dispose() => Marshal.Release(Instance);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint2
    {
        public double X;
        public double Y;
        public NativePoint2(Point2DValue point) { X = point.X; Y = point.Y; }
        public readonly Point2DValue Value => new(X, Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint3
    {
        public double X;
        public double Y;
        public double Z;
        public NativePoint3(Point3DValue point) { X = point.X; Y = point.Y; Z = point.Z; }
        public readonly Point3DValue Value => new(X, Y, Z);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public NativePoint3 Origin;
        public NativePoint3 AxisX;
        public NativePoint3 AxisZ;
        public NativePlacement(Placement3DValue placement)
        {
            Origin = new(placement.Origin); AxisX = new(placement.AxisX); AxisZ = new(placement.AxisZ);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Point3Out(IntPtr self, out NativePoint3 result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Point2Out(IntPtr self, out NativePoint2 result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DoublePoint2Out(IntPtr self, double parameter, out NativePoint2 result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PlacementIn(IntPtr self, NativePlacement placement);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PlacementOut(IntPtr self, NativePoint3 origin, NativePoint3 axisX, NativePoint3 axisZ, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Line2Out(IntPtr self, NativePoint2 start, NativePoint2 end, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ArrayObjectOut(IntPtr self, IntPtr array, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GuidOut(IntPtr self, out Guid result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GuidObjectOut(IntPtr self, Guid id, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StringObjectOut(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string argument, out IntPtr result);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern IntPtr SafeArrayCreateVector(ushort variantType, int lowerBound, uint count);
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int SafeArrayPutElement(IntPtr array, ref int index, IntPtr value);
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int SafeArrayDestroy(IntPtr array);

    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeDispatch
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint locale, out ITypeInfo typeInfo);
    }
}
