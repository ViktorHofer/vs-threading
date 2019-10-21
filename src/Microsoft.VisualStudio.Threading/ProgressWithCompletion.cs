﻿/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// An incremental progress reporting mechanism that also allows
    /// asynchronous awaiting for all reports to be processed.
    /// </summary>
    /// <typeparam name="T">The type of message sent in progress updates.</typeparam>
    public class ProgressWithCompletion<T> : IProgress<T>
    {
        /// <summary>
        /// The synchronization object.
        /// Applicable only when <see cref="joinableTaskFactory"/> is null.
        /// </summary>
        private readonly object syncObject;

        /// <summary>
        /// The handler to invoke for each progress update.
        /// </summary>
        private readonly Func<T, Task> handler;

        /// <summary>
        /// The set of progress reports that have started (but may not have finished yet).
        /// Applicable only when <see cref="joinableTaskFactory"/> is null.
        /// </summary>
        private readonly HashSet<Task> outstandingTasks;

        /// <summary>
        /// The factory to use for invoking the <see cref="handler"/>.
        /// Applicable only when <see cref="joinableTaskFactory"/> is null.
        /// </summary>
        private readonly TaskFactory taskFactory;

        /// <summary>
        /// A value indicating whether this instance was constructed on the main thread.
        /// Applicable only when <see cref="joinableTaskFactory"/> is not null.
        /// </summary>
        private readonly bool createdOnMainThread;

        /// <summary>
        /// The <see cref="JoinableTaskFactory"/> to use when invoking the <see cref="handler"/> to mitigate deadlocks.
        /// May be null.
        /// </summary>
        private readonly JoinableTaskFactory joinableTaskFactory;

        /// <summary>
        /// A collection of outstanding progress updates that have not completed execution.
        /// Applicable only when <see cref="joinableTaskFactory"/> is not null.
        /// </summary>
        private readonly JoinableTaskCollection outstandingJoinableTasks;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressWithCompletion{T}" /> class.
        /// </summary>
        /// <param name="handler">
        /// A handler to invoke for each reported progress value.
        /// Depending on the <see cref="SynchronizationContext"/> instance that is captured when this constructor is invoked,
        /// it is possible that this handler instance could be invoked concurrently with itself.
        /// </param>
        public ProgressWithCompletion(Action<T> handler)
            : this(WrapSyncHandler(handler), joinableTaskFactory: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressWithCompletion{T}" /> class.
        /// </summary>
        /// <param name="handler">
        /// A handler to invoke for each reported progress value.
        /// Depending on the <see cref="SynchronizationContext"/> instance that is captured when this constructor is invoked,
        /// it is possible that this handler instance could be invoked concurrently with itself.
        /// </param>
        public ProgressWithCompletion(Func<T, Task> handler)
            : this(handler, joinableTaskFactory: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressWithCompletion{T}" /> class.
        /// </summary>
        /// <param name="handler">
        /// A handler to invoke for each reported progress value.
        /// It is possible that this handler instance could be invoked concurrently with itself.
        /// </param>
        /// <param name="joinableTaskFactory">A <see cref="JoinableTaskFactory"/> instance that can be used to mitigate deadlocks when <see cref="WaitAsync(CancellationToken)"/> is called and the <paramref name="handler"/> requires the main thread.</param>
        public ProgressWithCompletion(Action<T> handler, JoinableTaskFactory? joinableTaskFactory)
            : this(WrapSyncHandler(handler), joinableTaskFactory)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressWithCompletion{T}" /> class.
        /// </summary>
        /// <param name="handler">
        /// A handler to invoke for each reported progress value.
        /// It is possible that this handler instance could be invoked concurrently with itself.
        /// </param>
        /// <param name="joinableTaskFactory">A <see cref="JoinableTaskFactory"/> instance that can be used to mitigate deadlocks when <see cref="WaitAsync(CancellationToken)"/> is called and the <paramref name="handler"/> requires the main thread.</param>
        public ProgressWithCompletion(Func<T, Task> handler, JoinableTaskFactory? joinableTaskFactory)
        {
            Requires.NotNull(handler, nameof(handler));
            this.handler = handler;
            if (joinableTaskFactory != null)
            {
                this.joinableTaskFactory = joinableTaskFactory;
                this.outstandingJoinableTasks = joinableTaskFactory.Context.CreateCollection();
                this.createdOnMainThread = joinableTaskFactory.Context.IsOnMainThread;
            }
            else
            {
                this.syncObject = new object();
                this.taskFactory = new TaskFactory(SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default);
                this.outstandingTasks = new HashSet<Task>();
            }
        }

        /// <summary>
        /// Receives a progress update.
        /// </summary>
        /// <param name="value">The value representing the updated progress.</param>
        void IProgress<T>.Report(T value)
        {
            this.Report(value);
        }

        /// <summary>
        /// Receives a progress update.
        /// </summary>
        /// <param name="value">The value representing the updated progress.</param>
        protected virtual void Report(T value)
        {
            if (this.joinableTaskFactory != null)
            {
                JoinableTask joinableTask = this.joinableTaskFactory.RunAsync(
                    async delegate
                    {
                        // Emulate the behavior of having captured a SynchronizationContext by invoking the handler on the main thread
                        // if the constructor was on the main thread. Otherwise use the threadpool thread. But never invoke the handler
                        // inline with our caller, per the behavior folks expect from this and .NET's Progress<T> class.
                        if (this.createdOnMainThread)
                        {
                            await this.joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                        }
                        else
                        {
                            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                        }

                        await this.handler(value).ConfigureAwaitRunInline();
                    });
                this.outstandingJoinableTasks.Add(joinableTask);
            }
            else
            {
#pragma warning disable CA2008 // Do not create tasks without passing a TaskScheduler
                var reported = this.taskFactory.StartNew(() => this.handler(value)).Unwrap();
#pragma warning restore CA2008 // Do not create tasks without passing a TaskScheduler
                lock (this.syncObject)
                {
                    this.outstandingTasks.Add(reported);
                }

                reported.ContinueWith(
                    t =>
                    {
                        lock (this.syncObject)
                        {
                            this.outstandingTasks.Remove(t);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.NotOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Returns a task that completes when all reported progress has executed.
        /// </summary>
        /// <returns>A task that completes when all progress is complete.</returns>
        public Task WaitAsync() => this.WaitAsync(CancellationToken.None);

        /// <summary>
        /// Returns a task that completes when all reported progress has executed.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task that completes when all progress is complete.</returns>
        public Task WaitAsync(CancellationToken cancellationToken)
        {
            if (this.outstandingJoinableTasks != null)
            {
                return this.outstandingJoinableTasks.JoinTillEmptyAsync(cancellationToken);
            }
            else
            {
                lock (this.syncObject)
                {
                    return Task.WhenAll(this.outstandingTasks).WithCancellation(cancellationToken);
                }
            }
        }

        private static Func<T, Task> WrapSyncHandler(Action<T> handler)
        {
            Requires.NotNull(handler, nameof(handler));
            return value =>
            {
                handler(value);
                return TplExtensions.CompletedTask;
            };
        }
    }
}
