﻿using Medallion.Threading.Redis;
using Medallion.Threading.Tests.Redis;
using Moq;
using NUnit.Framework;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Threading.Tests.Redis
{
    public abstract class RedisSynchronizationCoreTestCases<TLockProvider>
        // note: we arbitrarily use the single db provider because we will be overriding the set of dbs and so we don't
        // want to see cases for each possible db provider type
        where TLockProvider : TestingLockProvider<TestingRedisSynchronizationStrategy<TestingRedis3DatabaseProvider>>, new()
    {
        private TLockProvider _provider = default!;

        [SetUp]
        public void SetUp() => this._provider = new TLockProvider();

        [TearDown]
        public void TearDown() => this._provider.Dispose();

        [Test]
        public void TestMajorityFaultingDatabasesCauseAcquireToThrow()
        {
            CheckProviderCompatibility();

            var databases = Enumerable.Range(0, 3).Select(_ => CreateDatabaseMock()).ToArray();
            MockDatabase(databases[0], () => throw new TimeZoneNotFoundException());
            MockDatabase(databases[2], () => throw new ArrayTypeMismatchException());

            this._provider.Strategy.DatabaseProvider.Databases = databases.Select(d => d.Object).ToArray();
            var @lock = this._provider.CreateLock("multi");

            // we only get the one exception
            Assert.ThrowsAsync<TimeZoneNotFoundException>(() => @lock.TryAcquireAsync().AsTask());

            // single sync acquire flow is different
            this._provider.Strategy.DatabaseProvider.Databases = new[] { databases[2].Object };
            var singleDatabaseLock = this._provider.CreateLock("single");
            Assert.Throws<ArrayTypeMismatchException>(() => singleDatabaseLock.Acquire());
        }

        [Test]
        [NonParallelizable] // timing-sensitive
        public async Task TestMajorityHangingDatabasesCauseAcquireToFail()
        {
            CheckProviderCompatibility();

            using var @event = new ManualResetEventSlim(initialState: false);
            var databases = Enumerable.Range(0, 3).Select(_ => CreateDatabaseMock()).ToArray();
            MockDatabase(databases[1], () => { @event.Wait(); return true; });
            MockDatabase(databases[2], () => { @event.Wait(); return false; });

            this._provider.Strategy.DatabaseProvider.Databases = databases.Select(d => d.Object).ToArray();
            // use a high min validity time so that TryAcquireAsync() can return very quickly despite the hang
            this._provider.Strategy.SetOptions(o => o.MinValidityTime(RedisDistributedLockOptionsBuilder.DefaultExpiry.TimeSpan - TimeSpan.FromSeconds(.2)));
            var @lock = this._provider.CreateLock("lock");

            Assert.IsNull(await @lock.TryAcquireAsync());

            @event.Set(); // just to free the waiting threads
        }

        [Test]
        public void TestMajorityFaultingDatabasesCauseReleaseToThrow()
        {
            CheckProviderCompatibility();

            var databases = Enumerable.Range(0, 5).Select(_ => CreateDatabaseMock()).ToArray();
            this._provider.Strategy.DatabaseProvider.Databases = databases.Select(d => d.Object).ToArray();
            var @lock = this._provider.CreateLock("lock");
            using var handle = @lock.Acquire();

            new List<int> { 1, 2, 4 }.ForEach(i => MockDatabase(databases[i], () => throw new DataMisalignedException()));
            var aggregateException = Assert.Throws<AggregateException>(() => handle.Dispose());
            Assert.IsInstanceOf<DataMisalignedException>(aggregateException.InnerException);
        }

        [Test]
        [NonParallelizable, Retry(tryCount: 3)] // timing-sensitive
        public async Task TestAcquireFailsIfItTakesTooLong([Values] bool synchronous)
        {
            CheckProviderCompatibility();

            var database = CreateDatabaseMock();
            MockDatabase(database, () => { Thread.Sleep(50); return true; });

            this._provider.Strategy.DatabaseProvider.Databases = new[] { database.Object };
            this._provider.Strategy.SetOptions(o => o.MinValidityTime(RedisDistributedLockOptionsBuilder.DefaultExpiry.TimeSpan - TimeSpan.FromMilliseconds(10)));
            var @lock = this._provider.CreateLock("lock");

            // single sync acquire has different timeout logic, so we test it separately
            Assert.IsNull(synchronous ? @lock.TryAcquire() : await @lock.TryAcquireAsync());
        }

        [Test]
        [NonParallelizable] // timing-sensitive
        public async Task TestFailedAcquireReleasesWhatHasAlreadyBeenAcquired()
        {
            CheckProviderCompatibility();

            using var @event = new ManualResetEventSlim();
            var failDatabase = CreateDatabaseMock();
            MockDatabase(failDatabase, () => { @event.Wait(); return false; });

            this._provider.Strategy.DatabaseProvider.Databases = new[] { RedisServer.GetDefaultServer(0).Multiplexer.GetDatabase(), failDatabase.Object };
            var @lock = this._provider.CreateLock("lock");

            var acquireTask = @lock.TryAcquireAsync().AsTask();
            Assert.IsFalse(acquireTask.Wait(TimeSpan.FromMilliseconds(50)));
            @event.Set();
            Assert.IsNull(await acquireTask);

            this._provider.Strategy.DatabaseProvider.Databases = new[] { RedisServer.GetDefaultServer(0).Multiplexer.GetDatabase() };
            var singleDatabaseLock = this._provider.CreateLock("lock");
            using var handle = await singleDatabaseLock.TryAcquireAsync();
            Assert.IsNotNull(handle);
        }

        // todo test even number of dbs with 2 failing

        private static Mock<IDatabase> CreateDatabaseMock()
        {
            var mock = new Mock<IDatabase>(MockBehavior.Strict);
            MockDatabase(mock, () => true);
            return mock;
        }

        private static void MockDatabase(Mock<IDatabase> mockDatabase, Func<bool> returns)
        {
            mockDatabase.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns(returns);
            mockDatabase.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns(() => Task.Run(returns));
            mockDatabase.Setup(d => d.ScriptEvaluate(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .Returns(() => RedisResult.Create(returns()));
            mockDatabase.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .Returns(() => Task.Run(() => RedisResult.Create(returns())));
            mockDatabase.Setup(d => d.SortedSetRemove(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns(() => (bool)RedisResult.Create(returns()));
            mockDatabase.Setup(d => d.SortedSetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns(() => Task.Run(() => (bool)RedisResult.Create(returns())));
        }

        private static void CheckProviderCompatibility()
        {
            // The semaphore5 provider is not compatible with this test class because it will attempt to acquire locks upon creation
            // which will fail due to our mocked DB setup.
            if (typeof(TLockProvider).IsConstructedGenericType
                && typeof(TLockProvider).GetGenericTypeDefinition() == typeof(TestingSemaphore5AsMutexProvider<,>))
            {
                Assert.Pass("The provider is incompatible");
            }
        }
    }
}
