﻿#region BSD Licence
/* Copyright (c) 2013, Doxense SARL
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
	* Redistributions of source code must retain the above copyright
	  notice, this list of conditions and the following disclaimer.
	* Redistributions in binary form must reproduce the above copyright
	  notice, this list of conditions and the following disclaimer in the
	  documentation and/or other materials provided with the distribution.
	* Neither the name of the <organization> nor the
	  names of its contributors may be used to endorse or promote products
	  derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
#endregion

using System;
using FoundationDb.Client.Native;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FoundationDb.Client
{

	public static class Fdb
	{

		/// <summary>Default path to the native C API library</summary>
		public static string NativeLibPath = ".";

		/// <summary>Default path to the network thread tracing file</summary>
		public static string TracePath = null;

		internal static readonly ArraySegment<byte> Empty = new ArraySegment<byte>(new byte[0]);

		/// <summary>Keys cannot exceed 10,000 bytes</summary>
		internal const int MaxKeySize = 10 * 1000;

		/// <summary>Values cannot exceed 100,000 bytes</summary>
		internal const int MaxValueSize = 100 * 1000;

		/// <summary>Maximum size of total written keys and values by a transaction</summary>
		internal const int MaxTransactionWriteSize = 10 * 1024 * 1024;

		public static int GetMaxApiVersion()
		{
			return FdbNative.GetMaxApiVersion();
		}

		/// <summary>Returns true if the error code represents a success</summary>
		public static bool Success(FdbError code)
		{
			return code == FdbError.Success;
		}

		/// <summary>Returns true if the error code represents a failure</summary>
		public static bool Failed(FdbError code)
		{
			return code != FdbError.Success;
		}

		/// <summary>Throws an exception if the code represents a failure</summary>
		internal static void DieOnError(FdbError code)
		{
			if (Failed(code)) throw MapToException(code);
		}

		/// <summary>Return the error message matching the specified error code</summary>
		public static string GetErrorMessage(FdbError code)
		{
			return FdbNative.GetError(code);
		}

		/// <summary>Maps an error code into an Exception (to be throwned)</summary>
		/// <param name="code"></param>
		/// <returns>Exception object corresponding to the error code, or null if the code is not an error</returns>
		public static Exception MapToException(FdbError code)
		{
			if (code == FdbError.Success) return null;

			string msg = GetErrorMessage(code);
			if (true || msg == null) throw new InvalidOperationException(String.Format("Unexpected error code {0}", (int)code));

			switch(code)
			{
				//TODO!
				default: 
					throw new InvalidOperationException(msg);
			}
		}

		#region Key/Value serialization

		/// <summary>Serialize an unicode string into a key</summary>
		public static ArraySegment<byte> GetKeyBytes(string key)
		{
			if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty.", "key");
			return new ArraySegment<byte>(Encoding.UTF8.GetBytes(key));
		}

		/// <summary>Ensures that a serialized key is valid</summary>
		internal static void EnsureKeyIsValid(ArraySegment<byte> key)
		{
			if (key.Count == 0) throw new ArgumentException("Key cannot be null or empty.", "key");
			if (key.Count > Fdb.MaxKeySize) throw new ArgumentException(String.Format("Key is too big ({0} > {1}).", key.Count, Fdb.MaxKeySize), "key");
			if (key.Array == null) throw new ArgumentException("Key cannot have a null buffer", "key.Array");
		}

		/// <summary>Serialize an unicode string into a value</summary>
		public static ArraySegment<byte> GetValueBytes(string value)
		{
			if (value == null) throw new ArgumentNullException("Value cannot be null.", "value");
			if (value.Length == 0) return Fdb.Empty;
			return new ArraySegment<byte>(Encoding.UTF8.GetBytes(value));
		}

		/// <summary>Ensures that a serialized value is valid</summary>
		internal static void EnsureValueIsValid(ArraySegment<byte> value)
		{
			if (value.Count == 0) throw new ArgumentNullException("value cannot be null.", "value");
			if (value.Count > Fdb.MaxValueSize) throw new ArgumentException(String.Format("Value is too big ({0} > {1}).", value.Count, Fdb.MaxValueSize), "value");
		}

		#endregion

		#region Network Thread / Event Loop...

		private static Thread s_eventLoop;
		private static bool s_eventLoopStarted;
		private static bool s_eventLoopRunning;
		private static int? s_eventLoopThreadId;

		/// <summary>Starts the thread running the FDB event loop</summary>
		private static void StartEventLoop()
		{
			if (s_eventLoop == null)
			{
				Debug.WriteLine("Starting Network Thread...");

				var thread = new Thread(new ThreadStart(EventLoop));
				thread.Name = "FoundationDB Network Thread";
				thread.IsBackground = true;
				thread.Priority = ThreadPriority.AboveNormal;
				s_eventLoop = thread;
				try
				{
					thread.Start();
					s_eventLoopStarted = true;
				}
				catch (Exception)
				{
					s_eventLoopStarted = false;
					s_eventLoop = null;
					throw;
				}
			}
		}

		/// <summary>Stops the thread running the FDB event loop</summary>
		private static void StopEventLoop()
		{
			if (s_eventLoopStarted)
			{
				Debug.WriteLine("Stopping Network Thread...");

				var err = FdbNative.StopNetwork();
				s_eventLoopStarted = false;

				var thread = s_eventLoop;
				if (thread != null && thread.IsAlive)
				{
					try
					{
						thread.Abort();
						thread.Join(TimeSpan.FromSeconds(1));
					}
					catch (ThreadAbortException)
					{
					}
					finally
					{
						s_eventLoop = null;
					}
				}
			}
		}

		/// <summary>Entry point for the Network Thread</summary>
		private static void EventLoop()
		{
			try
			{
				s_eventLoopRunning = true;

				s_eventLoopThreadId = Thread.CurrentThread.ManagedThreadId;
				Debug.WriteLine("FDB Event Loop running on thread #" + s_eventLoopThreadId.Value + "...");

				var err = FdbNative.RunNetwork();
				if (err != FdbError.Success)
				{ // Stop received
					Debug.WriteLine("RunNetwork returned " + err + " : " + GetErrorMessage(err));
				}
			}
			catch (Exception e)
			{
				if (e is ThreadAbortException)
				{ // bie bie
					Thread.ResetAbort();
					return;
				}
			}
			finally
			{
				Debug.WriteLine("FDB Event Loop stopped");
				s_eventLoopThreadId = null;
				s_eventLoopRunning = false;
			}
		}

		/// <summary>Returns 'true' if we are currently running on the Event Loop thread</summary>
		internal static bool IsNetworkThread
		{
			get
			{
				var eventLoopThreadId = s_eventLoopThreadId;
				return eventLoopThreadId.HasValue && Thread.CurrentThread.ManagedThreadId == eventLoopThreadId.Value;
			}
		}

		/// <summary>Throws if the current thread is the Network Thread.</summary>
		/// <remarks>Should be used to ensure that we do not execute tasks continuations from the network thread, to avoid dead-locks.</remarks>
		internal static void EnsureNotOnNetworkThread()
		{
#if DEBUG
			Debug.WriteLine("> [Executing on thread " + Thread.CurrentThread.ManagedThreadId + "]");
#endif

			if (Fdb.IsNetworkThread)
			{ // cannot commit from same thread as the network loop because it could lead to a deadlock
				FailCannotExecuteOnNetworkThread();
			}
		}

		private static void FailCannotExecuteOnNetworkThread()
		{
#if DEBUG
			if (Debugger.IsAttached) Debugger.Break();
#endif
			throw new InvalidOperationException("Cannot commit transaction from the Network Thread!");
		}

		#endregion

		#region Cluster...

		/// <summary>Opens a connection to the local FDB cluster</summary>
		/// <param name="ct"></param>
		/// <returns></returns>
		public static Task<FdbCluster> OpenLocalClusterAsync(CancellationToken ct = default(CancellationToken))
		{
			//BUGBUG: does 'null' means Local? or does it mean the default config file that may or may not point to the local cluster ??
			return OpenClusterAsync(null, ct);
		}

		/// <summary>Opens a connection to an FDB Cluster</summary>
		/// <param name="path">Path to the 'fdb.cluster' file, or null for default</param>
		/// <returns>Task that will return an FdbCluster, or an exception</returns>
		public static Task<FdbCluster> OpenClusterAsync(string path = null, CancellationToken ct = default(CancellationToken))
		{
			ct.ThrowIfCancellationRequested();

			EnsureIsStarted();

			Debug.WriteLine("Connecting to " + (path == null ? "default cluster" : ("cluster specified in " + path)));

			//TODO: check path ?
			var future = FdbNative.CreateCluster(path);

			return FdbFuture.CreateTaskFromHandle(future,
				(h) =>
				{
					ClusterHandle cluster;
					var err = FdbNative.FutureGetCluster(h, out cluster);
					if (err != FdbError.Success)
					{
						cluster.Dispose();
						throw MapToException(err);
					}
					return new FdbCluster(cluster, path);
				},
				ct
			);
		}

		#endregion

		#region Database...

		/// <summary>Open a database on the local cluster</summary>
		/// <param name="name">Name of the database. Must be 'DB'</param>
		/// <param name="ct">Cancellation Token</param>
		/// <returns>Task that will return an FdbDatabase, or an exception</returns>
		/// <remarks>As of Beta1, the only supported database name is 'DB'</remarks>
		/// <exception cref="System.InvalidOperationException">If <paramref name="name"/> is anything other than 'DB'</exception>
		/// <exception cref="System.OperationCanceledException">If the token <paramref name="ct"/> is cancelled</exception>
		public static async Task<FdbDatabase> OpenLocalDatabaseAsync(string name, CancellationToken ct = default(CancellationToken))
		{
			ct.ThrowIfCancellationRequested();

			Debug.WriteLine("Connecting to local database " + name + " ...");

			FdbCluster cluster = null;
			FdbDatabase db = null;
			bool success = false;
			try
			{
				cluster = await OpenLocalClusterAsync(ct);
				//note: since the cluster is not provided by the caller, link it with the database's Dispose()
				db = await cluster.OpenDatabaseAsync(name, true, ct);
				success = true;
				return db;
			}
			finally
			{
				if (!success)
				{
					// cleanup the cluter if something went wrong
					if (db != null) db.Dispose();
					if (cluster != null) cluster.Dispose();
				}
			}
		}

		#endregion

		/// <summary>Ensure that we have loaded the C API library, and that the Network Thread has been started</summary>
		private static void EnsureIsStarted()
		{
			if (!s_eventLoopStarted) Start();
		}

		/// <summary>Select the correct API version, and start the Network Thread</summary>
		public static void Start()
		{
			Debug.WriteLine("Selecting API version " + FdbNative.FDB_API_VERSION);

			DieOnError(FdbNative.SelectApiVersion(FdbNative.FDB_API_VERSION));

			Debug.WriteLine("Setting up Network Thread...");

			if (TracePath != null)
			{
				Debug.WriteLine("Will trace client activity in " + TracePath);
				// create trace directory if missing...
				if (!Directory.Exists(TracePath)) Directory.CreateDirectory(TracePath);

				unsafe
				{
					int n;
					var data = FdbNative.ToNativeString(TracePath, nullTerminated: true, length: out n);
					fixed (byte* ptr = data)
					{
						DieOnError(FdbNative.NetworkSetOption(FdbNetworkOption.TraceEnable, ptr, n));
					}
				}
			}

			DieOnError(FdbNative.SetupNetwork());
			Debug.WriteLine("Network has been set up");

			StartEventLoop();
		}

		/// <summary>Stop the Network Thread</summary>
		public static void Stop()
		{
			Debug.WriteLine("Stopping Network Thread");
			StopEventLoop();
			Debug.WriteLine("Stopped");
		}

	}

}