#nullable enable
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Weaver.Editor
{
    /// <summary>
    /// Tracks which methods are actively in a repetitive cycle. Once a method enters such a cycle, the goal is to no
    /// longer output call stacks corresponding to that method, but to mark it as repetitively being called.
    ///
    /// In addition to entering a repetitive cycle, a method can exit a repetitive cycle.
    /// This phenomenon is illustrated by the following set of calls:
    ///     ```
    /// A B C D E F G D D H D I D D D J K L M N D O
    /// (1)      \-(2)-/                  (5)
    ///     \-----(3)-----/
    ///     \-(4)-/
    ///     ```
    /// In this example, we're tracking a sliding window of the four most recent method calls.
    /// In the region marked as (2), we find that `D` is called 75% of the time, and we mark it as repetitive.
    /// The method stays marked that way until, in the region marked (4), we find that `D` has not been called for the
    /// entire duration of the sliding window, and the method is no longer marked as repetitive.
    ///
    /// At time (1) and in region (2), the call to `D` is logged with the full stack trace.
    /// In region (3), all calls to `C` are not logged at all, except that a counter is incremented to track how many
    /// times the method was called in the repetitive region. Finally, at time (5), the call to `D` logged with the full
    /// stack trace again.
    ///
    /// In region (3), calls to methods other than `D` are logged with the full stack trace, *unless* they are called
    /// *by* `D` itself.
    /// </summary>
    public class ActiveRepetitions
    {
        readonly ConcurrentDictionary<MethodName, ActiveRepetition> activelyRepetitiveMethodsByName = new();
        int stackSizeInsideExecutingRepetitiveMethod = 0;
        readonly long numCallsInWindowForRepetitiveness;
        static long numCallsBeforeNoLongerRepetitive;
        readonly SlidingWindowCounter window;

        public ActiveRepetitions(
            long windowSize,
            long numCallsInWindowForRepetitiveness,
            long numCallsBeforeNoLongerRepetitive)
        {
            window = new SlidingWindowCounter(windowSize);
            ActiveRepetitions.numCallsBeforeNoLongerRepetitive = numCallsBeforeNoLongerRepetitive;
            this.numCallsInWindowForRepetitiveness = numCallsInWindowForRepetitiveness;
        }

        /// <summary>
        /// Are we currently in the middle of calling a method that is marked as
        /// repetitive?
        /// </summary>
        bool isExecutingRepetitiveMethod => CurrentlyExecutingMethod() != null;

        public ActiveRepetition? CurrentlyExecutingMethod() => activelyRepetitiveMethodsByName
            .FirstOrDefault(x => x.Value.isExecuting).Value;

        public void OnMethodEnter(MethodName method, int frame)
        {
            if (isExecutingRepetitiveMethod)
            {
                // In the future, we will also track downstream calls of the
                // currently executing repetitive method. For now, all downstream
                // methods of the currently executing repetitive method are
                // discarded completely.

                stackSizeInsideExecutingRepetitiveMethod++;
                return;
            }

            if (activelyRepetitiveMethodsByName.ContainsKey(method))
            {
                activelyRepetitiveMethodsByName[method].OnEnter();
                return;
            }

            long numExecutionsInWindow = window.Add(method);
            if (numExecutionsInWindow > numCallsInWindowForRepetitiveness)
            {
                ActiveRepetition repetition = new ActiveRepetition(method);
                repetition.OnEnter();
                activelyRepetitiveMethodsByName.TryAdd(method, repetition);

                PrimitiveTracker.primitiveTraceSqliteOutput.StartRepetitiveRegion(method, frame);
            }
        }

        public void OnMethodExit(MethodName method, int frame)
        {
            activelyRepetitiveMethodsByName.TryGetValue(method, out ActiveRepetition? currExecutingMethod);
            if (currExecutingMethod != null)
            {
                if (stackSizeInsideExecutingRepetitiveMethod == 0)
                {
                    currExecutingMethod.OnExit();
                }
                else
                {
                    stackSizeInsideExecutingRepetitiveMethod--;
                }

                return;
            }

            foreach (ActiveRepetition activeRepetition in activelyRepetitiveMethodsByName.Values)
            {
                activeRepetition.MarkNotCalledOnCurrentStep();
                if (activeRepetition.isNoLongerRepetitive && activeRepetition.numCallsDuringRepetitiveRegion > 10)
                {
                    PrimitiveTracker.primitiveTraceSqliteOutput.EndRepetitiveRegion(
                        activeRepetition.method,
                        activeRepetition.numCallsDuringRepetitiveRegion,
                        frame
                    );
                }
            }

            IEnumerable<MethodName> toRemove = (from kvp in activelyRepetitiveMethodsByName
                where kvp.Value.isNoLongerRepetitive
                select kvp.Key);

            foreach (MethodName s in toRemove)
            {
                activelyRepetitiveMethodsByName.TryRemove(s, out ActiveRepetition _);
            }
        }

        /// <summary>
        /// At this point, all the method exits have taken place. However, there may still be active repetitions that
        /// will never be considered non-repetitive because the program has terminated.
        /// Send over the remaining repetitions as though they have all become non-repetitive at this time.
        /// </summary>
        void SendRemainingRepetitions(int frame)
        {
            foreach (ActiveRepetition activeRepetition in activelyRepetitiveMethodsByName.Values)
            {
                PrimitiveTracker.primitiveTraceSqliteOutput.EndRepetitiveRegion(
                    activeRepetition.method,
                    activeRepetition.numCallsDuringRepetitiveRegion,
                    frame
                );
            }

            activelyRepetitiveMethodsByName.Clear();
        }

        public class ActiveRepetition
        {
            public readonly MethodName method;

            public ActiveRepetition(MethodName method)
            {
                this.method = method;
            }

            int numStepsSinceLastCall = 0;
            public int numCallsDuringRepetitiveRegion = 0;

            public bool isExecuting = false;

            public bool isNoLongerRepetitive => numStepsSinceLastCall > numCallsBeforeNoLongerRepetitive;

            public void OnEnter()
            {
                numCallsDuringRepetitiveRegion++;
                numStepsSinceLastCall = 0;
                isExecuting = true;
            }

            public void OnExit()
            {
                isExecuting = false;
            }

            public void MarkNotCalledOnCurrentStep()
            {
                numStepsSinceLastCall++;
            }
        }
    }
}