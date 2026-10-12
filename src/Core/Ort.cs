using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FindAnything.Core
{
    /// <summary>
    /// The few ONNX Runtime calls the picture model needs, through its C API (no managed package, so nothing beyond
    /// onnxruntime.dll). The C API is a table of function pointers; the indices below are its order in
    /// onnxruntime_c_api.h for ORT_API_VERSION 20 (ONNX Runtime 1.20), which only ever grows at the end.
    /// </summary>
    internal sealed class Ort : IDisposable
    {
        private const int ApiVersion = 20;
        private const int I_GetErrorMessage = 2, I_CreateEnv = 3, I_DisableTelemetry = 6, I_CreateSession = 7, I_Run = 9,
            I_CreateSessionOptions = 10, I_SetGraphOpt = 23, I_SetIntraOpThreads = 24, I_GetInputCount = 30,
            I_GetOutputCount = 31, I_GetInputName = 36, I_GetOutputName = 37, I_CreateTensor = 49, I_GetTensorData = 51,
            I_GetDimsCount = 61, I_GetDims = 62, I_GetTypeAndShape = 65, I_CreateCpuMemoryInfo = 69, I_AllocatorFree = 76,
            I_GetDefaultAllocator = 78, I_ReleaseEnv = 92, I_ReleaseStatus = 93, I_ReleaseMemoryInfo = 94,
            I_ReleaseSession = 95, I_ReleaseValue = 96, I_ReleaseTypeAndShape = 99, I_ReleaseSessionOptions = 100;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetApiBaseFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetApiFn(uint version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr ErrMsgFn(IntPtr status);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CreateEnvFn(int level, [MarshalAs(UnmanagedType.LPStr)] string id, out IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr EnvFn(IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CreateSessionFn(IntPtr env, [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr opts, out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr RunFn(IntPtr session, IntPtr runOpts, IntPtr[] inNames, IntPtr[] inputs, UIntPtr nIn, IntPtr[] outNames, UIntPtr nOut, IntPtr[] outputs);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr OutPtrFn(out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr SetIntFn(IntPtr opts, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CountFn(IntPtr session, out UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr NameFn(IntPtr session, UIntPtr index, IntPtr allocator, out IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CreateTensorFn(IntPtr info, IntPtr data, UIntPtr bytes, long[] shape, UIntPtr dims, int type, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetDataFn(IntPtr value, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetShapeFn(IntPtr value, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr DimsCountFn(IntPtr info, out UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr DimsFn(IntPtr info, long[] dims, UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CpuInfoFn(int allocator, int memType, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FreeFn(IntPtr allocator, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ReleaseFn(IntPtr p);

        private readonly IntPtr api;
        private IntPtr env, memInfo;
        private readonly ErrMsgFn errMsg;
        private readonly ReleaseFn releaseStatus;

        public const int Float = 1, Int64 = 7;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        public Ort(string dllPath)
        {
            IntPtr lib = LoadLibrary(dllPath);
            if (lib == IntPtr.Zero) throw new InvalidOperationException("Could not load " + dllPath + " (error " + Marshal.GetLastWin32Error() + ")");
            IntPtr baseFn = GetProcAddress(lib, "OrtGetApiBase");
            if (baseFn == IntPtr.Zero) throw new InvalidOperationException("onnxruntime.dll has no OrtGetApiBase");
            IntPtr apiBase = F<GetApiBaseFn>(baseFn)();
            api = F<GetApiFn>(Marshal.ReadIntPtr(apiBase, 0))(ApiVersion);
            if (api == IntPtr.Zero) throw new InvalidOperationException("onnxruntime.dll is older than version 1.20");
            errMsg = Fn<ErrMsgFn>(I_GetErrorMessage);
            releaseStatus = Fn<ReleaseFn>(I_ReleaseStatus);
            Check(Fn<CreateEnvFn>(I_CreateEnv)(3 /* warnings */, "FindAnything", out env));
            Check(Fn<EnvFn>(I_DisableTelemetry)(env));   // no usage events
            Check(Fn<CpuInfoFn>(I_CreateCpuMemoryInfo)(0 /* device allocator */, 0, out memInfo));
        }

        private static T F<T>(IntPtr p) where T : class { return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T)); }
        private T Fn<T>(int index) where T : class { return F<T>(Marshal.ReadIntPtr(api, index * IntPtr.Size)); }

        private void Check(IntPtr status)
        {
            if (status == IntPtr.Zero) return;
            string msg = Marshal.PtrToStringAnsi(errMsg(status));
            releaseStatus(status);
            throw new InvalidOperationException("ONNX Runtime: " + msg);
        }

        /// <summary>A loaded model plus its input and output names.</summary>
        public sealed class Session
        {
            public IntPtr Handle;
            public string[] Inputs, Outputs;
        }

        public Session Open(string modelPath, int threads)
        {
            IntPtr opts;
            Check(Fn<OutPtrFn>(I_CreateSessionOptions)(out opts));
            try
            {
                Check(Fn<SetIntFn>(I_SetIntraOpThreads)(opts, threads));
                Check(Fn<SetIntFn>(I_SetGraphOpt)(opts, 99 /* all optimisations */));
                Session s = new Session();
                Check(Fn<CreateSessionFn>(I_CreateSession)(env, modelPath, opts, out s.Handle));
                s.Inputs = Names(s.Handle, I_GetInputCount, I_GetInputName);
                s.Outputs = Names(s.Handle, I_GetOutputCount, I_GetOutputName);
                return s;
            }
            finally { Fn<ReleaseFn>(I_ReleaseSessionOptions)(opts); }
        }

        private string[] Names(IntPtr session, int countIndex, int nameIndex)
        {
            UIntPtr n;
            Check(Fn<CountFn>(countIndex)(session, out n));
            IntPtr alloc;
            Check(Fn<OutPtrFn>(I_GetDefaultAllocator)(out alloc));
            string[] names = new string[(int)n.ToUInt32()];
            for (int i = 0; i < names.Length; i++)
            {
                IntPtr p;
                Check(Fn<NameFn>(nameIndex)(session, (UIntPtr)(uint)i, alloc, out p));
                names[i] = Marshal.PtrToStringAnsi(p);
                Check(Fn<FreeFn>(I_AllocatorFree)(alloc, p));
            }
            return names;
        }

        /// <summary>One input tensor: either float or long data, with its shape.</summary>
        public sealed class Input
        {
            public string Name;
            public float[] Floats;
            public long[] Longs;
            public long[] Shape;
        }

        /// <summary>Runs the model and returns the named output as floats (with its shape).</summary>
        public float[] Run(Session s, Input[] inputs, string output, out long[] shape)
        {
            GCHandle[] pins = new GCHandle[inputs.Length];
            IntPtr[] values = new IntPtr[inputs.Length];
            IntPtr[] inNames = new IntPtr[inputs.Length];
            IntPtr outName = Marshal.StringToHGlobalAnsi(output);
            IntPtr[] outs = new IntPtr[1];
            try
            {
                for (int i = 0; i < inputs.Length; i++)
                {
                    Input inp = inputs[i];
                    object data = inp.Floats != null ? (object)inp.Floats : inp.Longs;
                    int bytes = inp.Floats != null ? inp.Floats.Length * 4 : inp.Longs.Length * 8;
                    pins[i] = GCHandle.Alloc(data, GCHandleType.Pinned);
                    Check(Fn<CreateTensorFn>(I_CreateTensor)(memInfo, pins[i].AddrOfPinnedObject(), (UIntPtr)(uint)bytes,
                        inp.Shape, (UIntPtr)(uint)inp.Shape.Length, inp.Floats != null ? Float : Int64, out values[i]));
                    inNames[i] = Marshal.StringToHGlobalAnsi(inp.Name);
                }
                Check(Fn<RunFn>(I_Run)(s.Handle, IntPtr.Zero, inNames, values, (UIntPtr)(uint)inputs.Length,
                    new[] { outName }, (UIntPtr)1u, outs));

                IntPtr info;
                Check(Fn<GetShapeFn>(I_GetTypeAndShape)(outs[0], out info));
                try
                {
                    UIntPtr nd;
                    Check(Fn<DimsCountFn>(I_GetDimsCount)(info, out nd));
                    shape = new long[(int)nd.ToUInt32()];
                    Check(Fn<DimsFn>(I_GetDims)(info, shape, nd));
                }
                finally { Fn<ReleaseFn>(I_ReleaseTypeAndShape)(info); }
                long count = 1;
                foreach (long d in shape) count *= d;
                IntPtr p;
                Check(Fn<GetDataFn>(I_GetTensorData)(outs[0], out p));
                float[] result = new float[count];
                Marshal.Copy(p, result, 0, (int)count);
                return result;
            }
            finally
            {
                for (int i = 0; i < inputs.Length; i++)
                {
                    if (values[i] != IntPtr.Zero) Fn<ReleaseFn>(I_ReleaseValue)(values[i]);
                    if (pins[i].IsAllocated) pins[i].Free();
                    if (inNames[i] != IntPtr.Zero) Marshal.FreeHGlobal(inNames[i]);
                }
                if (outs[0] != IntPtr.Zero) Fn<ReleaseFn>(I_ReleaseValue)(outs[0]);
                Marshal.FreeHGlobal(outName);
            }
        }

        public void Close(Session s)
        {
            if (s != null && s.Handle != IntPtr.Zero) { Fn<ReleaseFn>(I_ReleaseSession)(s.Handle); s.Handle = IntPtr.Zero; }
        }

        public void Dispose()
        {
            if (memInfo != IntPtr.Zero) { Fn<ReleaseFn>(I_ReleaseMemoryInfo)(memInfo); memInfo = IntPtr.Zero; }
            if (env != IntPtr.Zero) { Fn<ReleaseFn>(I_ReleaseEnv)(env); env = IntPtr.Zero; }
        }
    }
}
