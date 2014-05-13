﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Jobs.Host.Bindings.BinderProviders;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Microsoft.Azure.Jobs
{
    // Used for launching an instance
    internal class RunnerProgram
    {
        private readonly CloudBlobDescriptor _parameterLogger;

        private RunnerProgram(CloudBlobDescriptor parameterLogger)
        {
            _parameterLogger = parameterLogger;
        }

        public static RunnerProgram Create(CloudBlobDescriptor parameterLogger)
        {
            return new RunnerProgram(parameterLogger);
        }

        public static FunctionExecutionResult MainWorker(CloudBlobDescriptor parameterLogger, FunctionInvokeRequest descr, IConfiguration config,
            CancellationToken cancellationToken)
        {
            RunnerProgram program = RunnerProgram.Create(parameterLogger);
            return MainWorker(() => program.Invoke(descr, config, cancellationToken));
        }

        private static FunctionExecutionResult MainWorker(Action invoke)
        {
            Console.WriteLine("running in pid: {0}", System.Diagnostics.Process.GetCurrentProcess().Id);
            Console.WriteLine("Timestamp:{0}", DateTime.Now.ToLongTimeString());

            FunctionExecutionResult result = new FunctionExecutionResult();

            try
            {
                invoke();
                // Success
                Console.WriteLine("Success");
            }
            catch (Exception e)
            {
                // both binding errors and user exceptions from the function will land here. 
                result.ExceptionType = e.GetType().FullName;
                result.ExceptionMessage = e.Message;

                // Failure. 
                Console.WriteLine("Exception while executing:");
                WriteExceptionChain(e);
                Console.WriteLine("FAIL");
            }

            return result;
        }

        // Write an exception and inner exceptions
        public static void WriteExceptionChain(Exception e)
        {
            Exception e2 = e;
            while (e2 != null)
            {
                Console.WriteLine("{0}, {1}", e2.GetType().FullName, e2.Message);

                // Write bonus information for extra diagnostics
                var se = e2 as StorageException;
                if (se != null)
                {
                    var nvc = se.RequestInformation.ExtendedErrorInformation.AdditionalDetails;

                    foreach (var key in nvc.Keys)
                    {
                        Console.WriteLine("  >{0}: {1}", key, nvc[key]);
                    }
                }

                Console.WriteLine(e2.StackTrace);
                Console.WriteLine();
                e2 = e2.InnerException;
            }
        }

        public void Invoke(FunctionInvokeRequest invoke, IConfiguration config, CancellationToken cancellationToken)
        {
            MethodInfo method = GetLocalMethod(invoke);
            IRuntimeBindingInputs inputs = new RuntimeBindingInputs(invoke.Location);
            Invoke(config, method, invoke.Id, inputs, invoke.Args, cancellationToken);
        }

        private static MethodInfo GetLocalMethod(FunctionInvokeRequest invoke)
        {
            var methodLocation = invoke.Location as MethodInfoFunctionLocation;
            if (methodLocation != null)
            {
                var method = methodLocation.MethodInfo;
                if (method != null)
                {
                    return method;
                }
            }

            throw new InvalidOperationException("Can't get a MethodInfo from function location:" + invoke.Location.ToString());

        }

        public static IConfiguration InitBinders()
        {
            Configuration config = new Configuration();

            AddDefaultBinders(config);
            return config;

        }

        public static void AddDefaultBinders(IConfiguration config)
        {
            // Blobs
            config.BlobBinders.Add(new CloudBlockBlobBinderProvider());
            config.BlobBinders.Add(new CloudPageBlobBinderProvider());
            config.BlobBinders.Add(new CloudBlobBinderProvider());
            config.BlobBinders.Add(new BlobStreamBinderProvider());
            config.BlobBinders.Add(new TextReaderProvider());
            config.BlobBinders.Add(new TextWriterProvider());
            config.BlobBinders.Add(new StringBlobBinderProvider());

            // Tables
            config.TableBinders.Add(new CloudTableBinderProvider());
            config.TableBinders.Add(new QueryableCloudTableBinderProvider());
            config.TableBinders.Add(new DictionaryTableBinderProvider());

            // Other
            config.Binders.Add(new CloudStorageAccountBinderProvider());
            config.Binders.Add(new CloudQueueBinderProvider());
            config.Binders.Add(new CancellationTokenBinderProvider());
            config.Binders.Add(new BinderBinderProvider()); // for IBinder
            config.Binders.Add(new Sdk1CloudStorageAccountBinderProvider());
        }

        internal static bool ShouldIgnoreInvokeString(Type parameterType)
        {
            // Work around problem using IBinder and CloudStorageAccount with Run/Replay from dashboard.
            if (parameterType == typeof(IBinder))
            {
                return true;
            }
            else if (parameterType == typeof(CloudStorageAccount))
            {
                return true;
            }
            else if (parameterType.Namespace == "Microsoft.WindowsAzure.Storage" && parameterType.Name == "CloudStorageAccount")
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public static void ApplyHooks(Type t, IConfiguration config)
        {
            var methodInit = t.GetMethod("Initialize",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
                new Type[] { typeof(IConfiguration) }, null);
            if (methodInit != null)
            {
                if (methodInit.IsStatic && methodInit.IsPublic)
                {
                    try
                    {
                        methodInit.Invoke(null, new object[] { config });
                    }
                    catch (TargetInvocationException ex)
                    {
                        // This will lose original callstack. Hopefully message is complete enough. 
                        if (ex.InnerException is InvalidOperationException)
                        {
                            throw ex.InnerException;
                        }
                    }
                }
            }
        }

        // Have to still pass in IRuntimeBindingInputs since methods can do binding at runtime. 
        private void Invoke(IConfiguration config, MethodInfo m, FunctionInstanceGuid instance,
            IRuntimeBindingInputs inputs, ParameterRuntimeBinding[] argDescriptors, CancellationToken cancellationToken)
        {
            int len = argDescriptors.Length;

            INotifyNewBlob notificationService = new NotifyNewBlobViaInMemory();


            IBinderEx bindingContext = new BindingContext(config, inputs, instance, notificationService, cancellationToken);

            BindResult[] binds = new BindResult[len];
            ParameterInfo[] ps = m.GetParameters();
            for (int i = 0; i < len; i++)
            {
                var p = ps[i];
                try
                {
                    binds[i] = argDescriptors[i].Bind(config, bindingContext, p);
                }
                catch (Exception e)
                {
                    string msg = string.Format("Error while binding parameter #{0} '{1}':{2}", i, p, e.Message);
                    binds[i] = new NullBindResult(msg) { IsErrorResult = true };
                }
            }

            Console.WriteLine("Parameters bound. Invoking user function.");
            Console.WriteLine("--------");

            SelfWatch fpStopWatcher = null;
            try
            {
                fpStopWatcher = InvokeWorker(m, binds, ps);
            }
            finally
            {
                // Process any out parameters, do any cleanup
                // For update, do any cleanup work. 

                // Ensure queue OnPostAction is called in PostActionOrder. This ordering is particularly important
                // for ensuring queue outputs occur last. That way, all other function side-effects are guaranteed to
                // have occurred by the time messages are enqueued.
                int[] bindResultIndicesInPostActionOrder = SortBindResultIndicesInPostActionOrder(binds);

                try
                {
                    Console.WriteLine("--------");

                    foreach (int bindResultIndex in bindResultIndicesInPostActionOrder)
                    {
                        var bind = binds[bindResultIndex];
                        try
                        {
                            // This could invoke user code and do complex things that may fail. Catch the exception 
                            bind.OnPostAction();
                        }
                        catch (Exception e)
                        {
                            string msg = string.Format("Error while handling parameter #{0} '{1}' after function returned:", bindResultIndex, ps[bindResultIndex]);
                            throw new InvalidOperationException(msg, e);
                        }
                    }
                }
                finally
                {
                    // Stop the watches last. PostActions may do things that should show up in the watches.
                    // PostActions could also take a long time (flushing large caches), and so it's useful to have
                    // watches still running.                
                    if (fpStopWatcher != null)
                    {
                        fpStopWatcher.Stop();
                    }
                }
            }
        }

        private static int[] SortBindResultIndicesInPostActionOrder(BindResult[] original)
        {
            int[] indices = new int[original.Length];
            for (int index = 0; index < indices.Length; index++)
            {
                indices[index] = index;
            }
            BindResult[] copy = new BindResult[indices.Length];
            Array.Copy(original, copy, original.Length);
            Array.Sort(copy, indices, PostActionOrderComparer.Instance);
            return indices;
        }

        private SelfWatch InvokeWorker(MethodInfo m, BindResult[] binds, ParameterInfo[] ps)
        {
            SelfWatch fpStopWatcher = null;
            if (_parameterLogger != null)
            {
                CloudBlockBlob blobResults = _parameterLogger.GetBlockBlob();
                fpStopWatcher = new SelfWatch(binds, ps, blobResults);
            }

            // Watchers may tweak args, so do those second.
            object[] args = Array.ConvertAll(binds, bind => bind.Result);

            try
            {
                var hasBindErrors = binds.OfType<IMaybeErrorBindResult>().Any(r => r.IsErrorResult);
                if (!hasBindErrors)
                {
                    if (IsAsyncMethod(m))
                    {
                        InformNoAsyncSupport();
                    }

                    object returnValue = m.Invoke(null, args);
                    HandleFunctionReturnParameter(m, returnValue);
                }
                else
                {
                    throw new InvalidOperationException("Error while binding function parameters.");
                }
            }
            catch (TargetInvocationException e)
            {
                // $$$ Beware, this loses the stack trace from the user's invocation
                // Print stacktrace to console now while we have it.
                Console.WriteLine(e.InnerException.StackTrace);

                throw e.InnerException;
            }
            finally
            {
                // Copy back any ref/out parameters
                for (int i = 0; i < binds.Length; i++)
                {
                    binds[i].Result = args[i];
                }
            }

            return fpStopWatcher;
        }

        /// <summary>
        /// Handles the function return value and logs it, if necessary
        /// </summary>
        private static void HandleFunctionReturnParameter(MethodInfo m, object returnValue)
        {
            Type returnType = m.ReturnType;

            if (returnType == typeof(void))
            {
                // No need to do anything
                return;
            }
            else if (IsAsyncMethod(m))
            {
                Task t = returnValue as Task;
                t.Wait();

                if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    PropertyInfo resultProperty = returnType.GetProperty("Result");
                    object result = resultProperty.GetValue(returnValue);

                    LogReturnValue(result);
                }
            }
            else
            {
                LogReturnValue(returnValue);
            }
        }

        private static bool IsAsyncMethod(MethodInfo m)
        {
            Type returnType = m.ReturnType;

            return typeof(Task).IsAssignableFrom(returnType);
        }

        private static void InformNoAsyncSupport()
        {
            Console.WriteLine("Warning: This asynchronous method will be run synchronously.");
        }

        private static void LogReturnValue(object value)
        {
            Console.WriteLine("Return value: {0}", value != null ? value.ToString() : "<null>");
        }

        private class PostActionOrderComparer : IComparer<BindResult>
        {
            private static readonly PostActionOrderComparer _instance = new PostActionOrderComparer();

            private PostActionOrderComparer()
            {
            }

            public static PostActionOrderComparer Instance { get { return _instance; } }

            public int Compare(BindResult x, BindResult y)
            {
                if (x == null)
                {
                    return y == null ? 0 : -1;
                }

                if (y == null)
                {
                    return 1;
                }

                return ((int)x.PostActionOrder).CompareTo((int)y.PostActionOrder);
            }
        }
    }
}