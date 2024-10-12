using System;

namespace oChan.Interfaces
{
    /// <summary>
    /// Provides data for the ThreadDiscovered event.
    /// </summary>
    public class ThreadEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the thread that was discovered.
        /// </summary>
        public IThread Thread { get; }

        /// <summary>
        /// Initializes a new instance of the ThreadEventArgs class.
        /// </summary>
        /// <param name="thread">The thread that was discovered.</param>
        public ThreadEventArgs(IThread thread)
        {
            Thread = thread;
        }
    }
}