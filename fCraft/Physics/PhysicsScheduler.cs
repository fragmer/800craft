﻿/*800Craft Physics; Copyright Jonty800 and LaoTszy 2012*/
using System;
using System.Threading;
using fCraft.Physics;
using System.Diagnostics;
using System.Collections.Generic;
using Util = RandomMaze.MazeUtil;
using fCraft.Drawing;

namespace fCraft
{
	public enum TaskCategory
	{
		Physics,
		Scripting,
	}

public class PhysScheduler
	{
		private MinBinaryHeap<PhysicsTask, Int64> _tasks = new MinBinaryHeap<PhysicsTask, Int64>();
		private Stopwatch _watch = new Stopwatch(); //a good counter of elapsed milliseconds
		private World _owner;
		private EventWaitHandle _continue = new EventWaitHandle(false, EventResetMode.AutoReset);
		private EventWaitHandle _stop = new EventWaitHandle(false, EventResetMode.ManualReset);
		private Thread _thread;

		public bool Started { get { return null != _thread; } }

		public PhysScheduler(World owner)
		{
			_owner = owner;
			_watch.Reset();
			_watch.Start();
		}

		private void ProcessTasks()
		{
			WaitHandle[] handles = new WaitHandle[] { _continue, _stop };
			int timeout = Timeout.Infinite;
			for (; ; )
			{
				int w = WaitHandle.WaitAny(handles, timeout);
				if (1 == w) //stop
					break;
				PhysicsTask task;
				//check if there is a due task
				lock (_tasks)
				{
					if (_tasks.Size == 0) //sanity check
					{
						timeout = Timeout.Infinite;
						continue; //nothing to do
					}
					task = _tasks.Head();
					Int64 now = _watch.ElapsedMilliseconds;
					if (task.DueTime <= now) //due time!
						_tasks.RemoveHead();
					else
					{
						timeout = (int)(task.DueTime - now); //here the time difference should not exceed 24 days :)
						continue;
					}
				}
				int delay;
				//preform it
				try
				{
					delay = task.Deleted ? 0 : task.Perform(); //dont perform deleted tasks 
				}
				catch (Exception e)
				{
					delay = 0;
					Logger.Log(LogType.Error, "ProcessPhysicsTasks: " + e);
				}
				//decide what's next
				lock (_tasks)
				{
					Int64 now = _watch.ElapsedMilliseconds;
					if (delay > 0)
					{
						task.DueTime = now + delay;
						_tasks.Add(task);
					}
					timeout = _tasks.Size > 0 ? Math.Max((int)(_tasks.Head().DueTime - now), 0) : Timeout.Infinite; //here the time difference should not exceed 24 days :)
				}
			}
		}

		public void Start()
		{
			if (null!=_thread)
			{
				//log error, running already
				return;
			}
			if (_tasks.Size>0)
				_continue.Set();
			_thread = new Thread(ProcessTasks);
			_thread.Start();
		}

		public void Stop()
		{
			if (null==_thread)
			{
				//log error, not running
				return;
			}
			_stop.Set();
			if (_thread.Join(10000))
			{
				//blocked?
				_thread.Abort(); //very bad
			}
			_thread = null;
			_tasks.Clear();
		}

		public void AddTask(PhysicsTask task, int delay)
		{
			task.DueTime = _watch.ElapsedMilliseconds + delay;
			lock (_tasks)
			{
				_tasks.Add(task);
			}
			_continue.Set();
		}
	}

	/// <summary>
	/// Base class for physic tasks
	/// </summary>
public abstract class PhysicsTask : IHeapKey<Int64>
{
    /// <summary>
    /// the task due time in milliseconds since some start moment
    /// </summary>
    public Int64 DueTime;

    /// <summary>
    /// The flag indicating that task must not be performed when due.
    /// This flag is introduced because the heap doesnt allow deletion of elements.
    /// It is possible to implement but far too complicated than just marking elements like that.
    /// </summary>
    public bool Deleted = false;

    protected World _world;
    protected Map _map;

    protected PhysicsTask(World world) //a task must be created under the map syn root
    {
        if (null == world)
            throw new ArgumentNullException("world");
        //if the map is null the task will not be rescheduled
        //if (null == world.Map)
        //    throw new ArgumentException("world has no map");
        lock (world.SyncRoot)
        {
            _world = world;
            _map = world.Map;
        }
    }

    public Int64 GetHeapKey()
    {
        return DueTime;
    }

    /// <summary>
    /// Performs the action. The returned value is used as the reschedule delay. If 0 - the task is completed and
    /// should not be rescheduled
    /// </summary>
    /// <returns></returns>
    public int Perform()
    {
        lock (_world.SyncRoot)
        {
            if (null == _map || !ReferenceEquals(_map, _world.Map))
                return 0;
            return PerformInternal();
        }
    }
    /// <summary>
    /// The real implementation of the action
    /// </summary>
    /// <returns></returns>
    protected abstract int PerformInternal();

	protected void UpdateMap(BlockUpdate upd)
	{
		_map.SetBlock(upd.X, upd.Y, upd.Z, upd.BlockType);
		_map.QueueUpdate(upd);
	}
}

    public interface IHeapKey<out T> where T : IComparable
	{
		T GetHeapKey();
	}

	/// <summary>
	/// Min binary heap implementation.
	/// Note that the base array size is never decreased, i.e. Clear just moves the internal pointer to the first element.
	/// </summary>
	public class MinBinaryHeap<T, K> where T:IHeapKey<K> where K:IComparable
	{
		private readonly List<T> _heap = new List<T>();
		private int _free = 0;

		/// <summary>
		/// Adds an element.
		/// </summary>
		/// <param name="t">The t.</param>
		public void Add(T t)
		{
			int me = _free++;
			if (_heap.Count > me)
				_heap[me] = t;
			else
				_heap.Add(t);
			K myKey = t.GetHeapKey();
			while (me > 0)
			{
				int parent = ParentIdx(me);
				if (_heap[parent].GetHeapKey().CompareTo(myKey)<0)
					break;
				Swap(me, parent);
				me = parent;
			}
		}

		/// <summary>
		/// Head of this heap. This call assumes that size was checked before accessing the head element.
		/// </summary>
		/// <returns>Head element.</returns>
		public T Head()
		{
			return _heap[0];
		}

		/// <summary>
		/// Removes the head. This call assumes that size was checked before removing the head element.
		/// </summary>
		public void RemoveHead()
		{
			_heap[0] = _heap[--_free];
			_heap[_free] = default(T); //to enable garbage collection for the deleted item when necessary
			if (0 == _free)
				return;
			int me = 0;
			K myKey = _heap[0].GetHeapKey();

			for (; ; )
			{
				int kid1, kid2;
				Kids(me, out kid1, out kid2);
				if (kid1 >= _free)
					break;
				int minKid;
				K minKidKey;
				if (kid2 >= _free)
				{
					minKid = kid1;
					minKidKey = _heap[minKid].GetHeapKey();
				}
				else
				{
					K key1 = _heap[kid1].GetHeapKey();
					K key2 = _heap[kid2].GetHeapKey();
					if (key1.CompareTo(key2) < 0)
					{
						minKid = kid1;
						minKidKey = key1;
					}
					else
					{
						minKid = kid2;
						minKidKey = key2;
					}
				}
				if (myKey.CompareTo(minKidKey)>0)
				{
					Swap(me, minKid);
					me = minKid;
				}
				else
					break;
			}
		}

		/// <summary>
		/// Heap size.
		/// </summary>
		public int Size
		{
			get
			{
				return _free;
			}
		}

		public void Clear()
		{
			for (int i = 0; i < _free; ++i)
				_heap[i] = default(T); //enables garbage collecting for the deleted elements
			_free = 0;
		}

		private static int ParentIdx(int idx)
		{
			return (idx - 1) / 2;
		}

		private static void Kids(int idx, out int kid1, out int kid2)
		{
			kid1 = 2 * idx + 1;
			kid2 = kid1 + 1;
		}

		private void Swap(int i1, int i2)
		{
			T t = _heap[i1];
			_heap[i1] = _heap[i2];
			_heap[i2] = t;
		}
	}
}

