﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.Serialization;
using Akavache.Sqlite3;
using Microsoft.Reactive.Testing;
using Newtonsoft.Json;
using ReactiveUI;
using ReactiveUI.Xaml;
using ReactiveUI.Testing;
using Xunit;
using System.Threading;
using System.Reactive.Concurrency;

namespace Akavache.Tests
{
    public class UserObject
    {
        public string Bio { get; set; }
        public string Name { get; set; }
        public string Blog { get; set; }
    }

    public class UserModel
    {
        public UserModel(UserObject user)
        {
        }

        public string Name { get; set; }
        public int Age { get; set; }
    }

    public class ServiceProvider : IServiceProvider
    {
        public object GetService(Type t)
        {
            if (t == typeof(UserModel))
            {
                return new UserModel(new UserObject());
            }
            return null;
        }
    }

    [DataContract]
    public class DummyRoutedViewModel : ReactiveObject, IRoutableViewModel
    {
        public string UrlPathSegment { get { return "foo"; } }
        [DataMember] public IScreen HostScreen { get; private set; }

        Guid _ARandomGuid;
        [DataMember] public Guid ARandomGuid 
        {
            get { return _ARandomGuid; }
            set { this.RaiseAndSetIfChanged(ref _ARandomGuid, value); }
        }

        public DummyRoutedViewModel(IScreen screen)
        {
            HostScreen = screen;
        }
    }

    [DataContract]
    public class DummyAppBootstrapper : IScreen
    {
        [DataMember]
        public IRoutingState Router { get; protected set; }

        public DummyAppBootstrapper()
        {
            Router = new RoutingState();
            Router.NavigateAndReset.Execute(new DummyRoutedViewModel(this) { ARandomGuid =  Guid.NewGuid() });
        }
    }

    public abstract class BlobCacheExtensionsFixture
    {
        protected abstract IBlobCache CreateBlobCache(string path);

        [Fact]
        public void DownloadUrlTest()
        {
            string path;

            using (Utility.WithEmptyDirectory(out path))
            {
                var fixture = CreateBlobCache(path);
                using(fixture)
                {
                    var bytes = fixture.DownloadUrl(@"https://www.google.com/intl/en_com/images/srpr/logo3w.png").First();
                    Assert.True(bytes.Length > 0);
                }

                fixture.Shutdown.Wait();
            }
        }

        [Fact]
        public void ObjectsShouldBeRoundtrippable()
        {
            string path;
            var input = new UserObject() {Bio = "A totally cool cat!", Name = "octocat", Blog = "http://www.github.com"};
            UserObject result;

            using (Utility.WithEmptyDirectory(out path))
            {
                using (var fixture = CreateBlobCache(path))
                {
                    if (fixture is TestBlobCache) return;
                    fixture.InsertObject("key", input).First();
                }

                using (var fixture = CreateBlobCache(path))
                {
                    result = fixture.GetObjectAsync<UserObject>("key").First();
                }
            }

            Assert.Equal(input.Blog, result.Blog);
            Assert.Equal(input.Bio, result.Bio);
            Assert.Equal(input.Name, result.Name);
        }

        [Fact]
        public void ArraysShouldBeRoundtrippable()
        {
            string path;
            var input = new[] {new UserObject {Bio = "A totally cool cat!", Name = "octocat", Blog = "http://www.github.com"}, new UserObject {Bio = "zzz", Name = "sleepy", Blog = "http://example.com"}};
            UserObject[] result;

            using (Utility.WithEmptyDirectory(out path))
            {
                using (var fixture = CreateBlobCache(path))
                {
                    if (fixture is TestBlobCache) return;

                    fixture.InsertObject("key", input).First();
                }

                using (var fixture = CreateBlobCache(path))
                {
                    result = fixture.GetObjectAsync<UserObject[]>("key").First();
                }
            }

            Assert.Equal(input.First().Blog, result.First().Blog);
            Assert.Equal(input.First().Bio, result.First().Bio);
            Assert.Equal(input.First().Name, result.First().Name);
            Assert.Equal(input.Last().Blog, result.Last().Blog);
            Assert.Equal(input.Last().Bio, result.Last().Bio);
            Assert.Equal(input.Last().Name, result.Last().Name);
        }

        [Fact]
        public void ObjectsCanBeCreatedUsingObjectFactory()
        {
            string path;
            var input = new UserModel(new UserObject()) {Age = 123, Name = "Old"};
            UserModel result;

            using (Utility.WithEmptyDirectory(out path))
            {
                using (var fixture = CreateBlobCache(path))
                {
                    if (fixture is TestBlobCache) return;

                    fixture.InsertObject("key", input).First();
                }

                using (var fixture = CreateBlobCache(path))
                {
                    result = fixture.GetObjectAsync<UserModel>("key").First();
                }
            }

            Assert.Equal(input.Age, result.Age);
            Assert.Equal(input.Name, result.Name);
        }

        [Fact]
        public void ArraysShouldBeRoundtrippableUsingObjectFactory()
        {
            string path;
            var input = new[] {new UserModel(new UserObject()) {Age = 123, Name = "Old"}, new UserModel(new UserObject()) {Age = 123, Name = "Old"}};
            UserModel[] result;
            using (Utility.WithEmptyDirectory(out path))
            {
                using (var fixture = CreateBlobCache(path))
                {
                    if (fixture is TestBlobCache) return;

                    fixture.InsertObject("key", input).First();
                }

                using (var fixture = CreateBlobCache(path))
                {
                    result = fixture.GetObjectAsync<UserModel[]>("key").First();
                }
            }

            Assert.Equal(input.First().Age, result.First().Age);
            Assert.Equal(input.First().Name, result.First().Name);
            Assert.Equal(input.Last().Age, result.Last().Age);
            Assert.Equal(input.Last().Name, result.Last().Name);
        }

        [Fact]
        public void FetchFunctionShouldBeCalledOnceForGetOrFetchObject()
        {
            int fetchCount = 0;
            var fetcher = new Func<IObservable<Tuple<string, string>>>(() =>
            {
                fetchCount++;
                return Observable.Return(new Tuple<string, string>("Foo", "Bar"));
            });

            string path;
            using(Utility.WithEmptyDirectory(out path))
            {
                using(var fixture = CreateBlobCache(path))
                {
                    var result = fixture.GetOrFetchObject("Test", fetcher).First();
                    Assert.Equal("Foo", result.Item1);
                    Assert.Equal("Bar", result.Item2);
                    Assert.Equal(1, fetchCount);

                    // 2nd time around, we should be grabbing from cache
                    result = fixture.GetOrFetchObject("Test", fetcher).First();
                    Assert.Equal("Foo", result.Item1);
                    Assert.Equal("Bar", result.Item2);
                    Assert.Equal(1, fetchCount);

                    // Testing persistence makes zero sense for TestBlobCache
                    if (fixture is TestBlobCache) return;
                }

                using(var fixture = CreateBlobCache(path))
                {
                    var result = fixture.GetOrFetchObject("Test", fetcher).First();
                    Assert.Equal("Foo", result.Item1);
                    Assert.Equal("Bar", result.Item2);
                    Assert.Equal(1, fetchCount);
                }
            }
        }

        [Fact]
        public void FetchFunctionShouldDebounceConcurrentRequests()
        {
            (new TestScheduler()).With(sched =>
            {
                string path;
                using (Utility.WithEmptyDirectory(out path))
                {
                    int callCount = 0;
                    var fetcher = new Func<IObservable<int>>(() => 
                    {
                        callCount++;
                        return Observable.Return(42).Delay(TimeSpan.FromMilliseconds(1000), sched);
                    });

                    var fixture = CreateBlobCache(path);
                    try
                    {
                        var result1 = fixture.GetOrFetchObject("foo", fetcher).CreateCollection();

                        Assert.Equal(0, result1.Count);

                        sched.AdvanceToMs(250);

                        // Nobody's returned yet, cache is empty, we should have called the fetcher
                        // once to get a result
                        var result2 = fixture.GetOrFetchObject("foo", fetcher).CreateCollection();
                        Assert.Equal(0, result1.Count);
                        Assert.Equal(0, result2.Count);
                        Assert.Equal(1, callCount);

                        sched.AdvanceToMs(750);

                        // Same as above, result1-3 are all listening to the same fetch
                        var result3 = fixture.GetOrFetchObject("foo", fetcher).CreateCollection();
                        Assert.Equal(0, result1.Count);
                        Assert.Equal(0, result2.Count);
                        Assert.Equal(0, result3.Count);
                        Assert.Equal(1, callCount);

                        // Fetch returned, all three collections should have an item
                        sched.AdvanceToMs(1250);
                        Assert.Equal(1, result1.Count);
                        Assert.Equal(1, result2.Count);
                        Assert.Equal(1, result3.Count);
                        Assert.Equal(1, callCount);

                        // Making a new call, but the cache has an item, this shouldn't result
                        // in a fetcher call either
                        var result4 = fixture.GetOrFetchObject("foo", fetcher).CreateCollection();
                        sched.AdvanceToMs(2500);
                        Assert.Equal(1, result1.Count);
                        Assert.Equal(1, result2.Count);
                        Assert.Equal(1, result3.Count);
                        Assert.Equal(1, result4.Count);
                        Assert.Equal(1, callCount);

                        // Making a new call, but with a new key - this *does* result in a fetcher
                        // call. Result1-4 shouldn't get any new items, and at t=3000, we haven't
                        // returned from the call made at t=2500 yet
                        var result5 = fixture.GetOrFetchObject("bar", fetcher).CreateCollection();
                        sched.AdvanceToMs(3000);
                        Assert.Equal(1, result1.Count);
                        Assert.Equal(1, result2.Count);
                        Assert.Equal(1, result3.Count);
                        Assert.Equal(1, result4.Count);
                        Assert.Equal(0, result5.Count);
                        Assert.Equal(2, callCount);

                        // Everything is done, we should have one item in result5 now
                        sched.AdvanceToMs(4000);
                        Assert.Equal(1, result1.Count);
                        Assert.Equal(1, result2.Count);
                        Assert.Equal(1, result3.Count);
                        Assert.Equal(1, result4.Count);
                        Assert.Equal(1, result5.Count);
                        Assert.Equal(2, callCount);
                    }
                    finally
                    {
                        // Since we're in TestScheduler, we can't use the normal 
                        // using statement, we need to kick off the async dispose,
                        // then start the scheduler to let it run
                        fixture.Dispose();
                        sched.Start();
                    }
                }
            });
        }

        [Fact]
        public void FetchFunctionShouldPropagateThrownExceptionAsObservableException()
        {
            var fetcher = new Func<IObservable<Tuple<string, string>>>(() =>
            {
                throw new InvalidOperationException();
            });

            string path;
            using(Utility.WithEmptyDirectory(out path))
            {
                using(var fixture = CreateBlobCache(path))
                {
                    var result = fixture.GetOrFetchObject("Test", fetcher)
                        .Catch(Observable.Return(new Tuple<string, string>("one", "two"))).First();
                    Assert.Equal("one", result.Item1);
                    Assert.Equal("two", result.Item2);
                }
            }
        }

        [Fact]
        public void FetchFunctionShouldPropagateObservedExceptionAsObservableException()
        {
            var fetcher = new Func<IObservable<Tuple<string, string>>>(() =>
                Observable.Throw<Tuple<string, string>>(new InvalidOperationException()));

            string path;
            using (Utility.WithEmptyDirectory(out path))
            {
                var fixture = CreateBlobCache(path);
                using (fixture)
                {
                    var result = fixture.GetOrFetchObject("Test", fetcher)
                        .Catch(Observable.Return(new Tuple<string, string>("one", "two"))).First();
                    Assert.Equal("one", result.Item1);
                    Assert.Equal("two", result.Item2);
                }

                fixture.Shutdown.Wait();
            }
        }

        [Fact]
        public void GetOrFetchShouldRespectExpiration()
        {
            (new TestScheduler()).With(sched => 
            {
                string path;
                using (Utility.WithEmptyDirectory(out path))
                {
                    var fixture = CreateBlobCache(path);
                    using (fixture)
                    {
                        var result = default(string);
                        fixture.GetOrFetchObject("foo",
                            () => Observable.Return("bar"),
                            sched.Now + TimeSpan.FromMilliseconds(1000))
                            .Subscribe(x => result = x);

                        sched.AdvanceByMs(250);
                        Assert.Equal("bar", result);

                        fixture.GetOrFetchObject("foo",
                            () => Observable.Return("baz"),
                            sched.Now + TimeSpan.FromMilliseconds(1000))
                            .Subscribe(x => result = x);

                        sched.AdvanceByMs(250);
                        Assert.Equal("bar", result);

                        sched.AdvanceByMs(1000);
                        fixture.GetOrFetchObject("foo",
                            () => Observable.Return("baz"),
                            sched.Now + TimeSpan.FromMilliseconds(1000))
                            .Subscribe(x => result = x);

                        sched.AdvanceByMs(250);
                        Assert.Equal("baz", result);
                    }

                    fixture.Shutdown.Wait();
                }
            });
        }

        [Fact]
        public void GetAndFetchLatestShouldInvalidateObjectOnError()
        {
            var fetcher = new Func<IObservable<string>>(() =>
            {
                throw new InvalidOperationException();
            });

            string path;
            using (Utility.WithEmptyDirectory(out path))
            {
                var fixture = CreateBlobCache(path);

                using (fixture)
                {
                    if (fixture is TestBlobCache) return;

                    fixture.InsertObject("foo", "bar").First();

                    fixture.GetAndFetchLatest("foo", fetcher, shouldInvalidateOnError: true)
                        .Catch(Observable.Return("get and fetch latest error"))
                        .First();

                    var result = fixture.GetObjectAsync<string>("foo")
                         .Catch(Observable.Return("get error"))
                         .First();

                    Assert.Equal("get error", result);
                }

                fixture.Shutdown.Wait();
            }
        }

        [Fact]
        public void GetAndFetchLatestCallsFetchPredicate()
        {
            var fetchPredicateCalled = false;

            Func<DateTimeOffset, bool> fetchPredicate = d =>
            {
                fetchPredicateCalled = true;

                return true;
            };

            var fetcher = new Func<IObservable<string>>(() => Observable.Return("baz"));

            string path;
            using (Utility.WithEmptyDirectory(out path))
            {
                var fixture = CreateBlobCache(path);

                using (fixture)
                {
                    if (fixture is TestBlobCache) return;

                    fixture.InsertObject("foo", "bar").First();

                    fixture.GetAndFetchLatest("foo", fetcher, fetchPredicate)
                        .First();

                    Assert.True(fetchPredicateCalled);
                }

                fixture.Shutdown.Wait();
            }
        }

        [Fact]
        public void KeysByTypeTest()
        {
            string path;
            var input = new[] 
            { 
                "Foo",
                "Bar",
                "Baz"
            };

            var inputItems = input.Select(x => new UserObject() { Name = x, Bio = "A thing", }).ToArray();
            var fixture = default(IBlobCache);

            using (Utility.WithEmptyDirectory(out path))
            using (fixture = CreateBlobCache(path))
            {
                foreach(var item in input.Zip(inputItems, (Key, Value) => new { Key, Value }))
                {
                    fixture.InsertObject(item.Key, item.Value).Wait();
                }

                var allObjectsCount = fixture.GetAllObjects<UserObject>().Select(x => x.Count()).First();
                Assert.Equal(input.Length, fixture.GetAllKeys().Count());
                Assert.Equal(input.Length, allObjectsCount);

                fixture.InsertObject("Quux", new UserModel(null)).Wait();

                allObjectsCount = fixture.GetAllObjects<UserObject>().Select(x => x.Count()).First();
                Assert.Equal(input.Length + 1, fixture.GetAllKeys().Count());
                Assert.Equal(input.Length, allObjectsCount);

                fixture.InvalidateObject<UserObject>("Foo").Wait();

                allObjectsCount = fixture.GetAllObjects<UserObject>().Select(x => x.Count()).First();
                Assert.Equal(input.Length + 1 - 1, fixture.GetAllKeys().Count());
                Assert.Equal(input.Length - 1, allObjectsCount);

                fixture.InvalidateAllObjects<UserObject>().Wait();

                allObjectsCount = fixture.GetAllObjects<UserObject>().Select(x => x.Count()).First();
                Assert.Equal(1, fixture.GetAllKeys().Count());
                Assert.Equal(0, allObjectsCount);

                fixture.Dispose();
                fixture.Shutdown.Wait();
            }
        }

        [Fact]
        public void GetAllKeysSmokeTest()
        {
            string path;

            using (Utility.WithEmptyDirectory(out path))
            {
                var fixture = default(IBlobCache);
                using (fixture = CreateBlobCache(path))
                {
                    Observable.Merge(
                        fixture.InsertObject("Foo", "bar"),
                        fixture.InsertObject("Bar", 10),
                        fixture.InsertObject("Baz", new UserObject() { Bio = "Bio", Blog = "Blog", Name = "Name" })
                    ).Last();

                    var keys = fixture.GetAllKeys();
                    Assert.Equal(3, keys.Count());
                    Assert.True(keys.Any(x => x.Contains("Foo")));
                    Assert.True(keys.Any(x => x.Contains("Bar")));
                }

                fixture.Shutdown.Wait();
                    
                if (fixture is TestBlobCache) return;

                using (fixture = CreateBlobCache(path))
                {
                    var keys = fixture.GetAllKeys();
                    Assert.Equal(3, keys.Count());
                    Assert.True(keys.Any(x => x.Contains("Foo")));
                    Assert.True(keys.Any(x => x.Contains("Bar")));
                }
            }
        }
    }

    public class PersistentBlobCacheExtensionsFixture : BlobCacheExtensionsFixture
    {
        protected override IBlobCache CreateBlobCache(string path)
        {
            BlobCache.ApplicationName = "TestRunner";
            return new BlockingDisposeCache(new TEncryptedBlobCache(path));
        }
    }

    public class SqliteBlobCacheExtensionsFixture : BlobCacheExtensionsFixture
    {
        protected override IBlobCache CreateBlobCache(string path)
        {
            BlobCache.ApplicationName = "TestRunner";
            return new BlockingDisposeObjectCache(new SqlitePersistentBlobCache(Path.Combine(path, "sqlite.db")));
        }
    }

    public class EncryptedSqliteBlobCacheExtensionsFixture : BlobCacheExtensionsFixture
    {
        protected override IBlobCache CreateBlobCache(string path)
        {
            BlobCache.ApplicationName = "TestRunner";
            return new BlockingDisposeObjectCache(new Sqlite3.EncryptedBlobCache(Path.Combine(path, "sqlite.db")));
        }
    }

    public class TestBlobCacheFixture : BlobCacheExtensionsFixture
    {
        protected override IBlobCache CreateBlobCache(string path)
        {
            BlobCache.ApplicationName = "TestRunner";
            return new TestBlobCache(RxApp.MainThreadScheduler);
        }
    }

    class BlockingDisposeCache : IBlobCache
    {
        protected readonly IBlobCache _inner;
        public BlockingDisposeCache(IBlobCache cache)
        {
            BlobCache.EnsureInitialized();
            _inner = cache;
        }

        public virtual void Dispose()
        {
            _inner.Dispose();
            _inner.Shutdown.Wait();
        }

        public IObservable<Unit> Insert(string key, byte[] data, DateTimeOffset? absoluteExpiration = null)
        {
            return _inner.Insert(key, data, absoluteExpiration);
        }

        public IObservable<byte[]> GetAsync(string key)
        {
            return _inner.GetAsync(key);
        }

        public IEnumerable<string> GetAllKeys()
        {
            return _inner.GetAllKeys();
        }

        public IObservable<DateTimeOffset?> GetCreatedAt(string key)
        {
            return _inner.GetCreatedAt(key);
        }

        public IObservable<Unit> Flush()
        {
            return _inner.Flush();
        }

        public IObservable<Unit> Invalidate(string key)
        {
            return _inner.Invalidate(key);
        }

        public IObservable<Unit> InvalidateAll()
        {
            return _inner.InvalidateAll();
        }

        public IObservable<Unit> Shutdown
        {
            get { return _inner.Shutdown; }
        }

        public IScheduler Scheduler
        {
            get { return _inner.Scheduler; }
        }
    }

    class BlockingDisposeObjectCache : BlockingDisposeCache, IObjectBlobCache
    {
        public BlockingDisposeObjectCache(IObjectBlobCache cache) : base(cache) { }

        public IObservable<Unit> InsertObject<T>(string key, T value, DateTimeOffset? absoluteExpiration = null)
        {
            return ((IObjectBlobCache)_inner).InsertObject(key, value, absoluteExpiration);
        }

        public IObservable<T> GetObjectAsync<T>(string key, bool noTypePrefix = false)
        {
            return ((IObjectBlobCache)_inner).GetObjectAsync<T>(key, noTypePrefix);
        }

        public IObservable<IEnumerable<T>> GetAllObjects<T>()
        {
            return ((IObjectBlobCache)_inner).GetAllObjects<T>();
        }

        public IObservable<Unit> InvalidateObject<T>(string key)
        {
            return ((IObjectBlobCache)_inner).InvalidateObject<T>(key);
        }

        public IObservable<Unit> InvalidateAllObjects<T>()
        {
            return ((IObjectBlobCache)_inner).InvalidateAllObjects<T>();
        }
    }
}
