using System;
using System.Threading;
using FastTests;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_4563 : RavenTestBase
    {
        
        [Fact]
        public void bulk_insert_throws_when_server_is_down()
        {
            DoNotReuseServer();

            using (var store = GetDocumentStore())
            {
                Exception exp = null;
                for (var run = 0; run < 5; run++)
                {
                    try
                    {
                        using (var bulkInsert = store.BulkInsert())
                        {
                            if (run == 0)
                                continue;

                            for (var j = 0; j < 10000; j++)
                            {
                                bulkInsert.Store(new Sample());

                                if (j == 5000 && run == 2)
                                {
                                    Server.Dispose();
                                    Thread.Sleep(100);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        exp = e;
                    }
                    finally
                    {
                        switch (run)
                        {
                            case 0:
                                Assert.Equal(null, exp);
                                break;
                            case 1:
                                Assert.Equal(null, exp);
                                break;
                            case 2:
                                Assert.True(exp.Message.StartsWith("Write to stream faild at"));
                                break;
                            case 3:
                                Assert.True(exp.Message.StartsWith("Write to stream faild at"));
                                break;
                            case 4:
                                Assert.True(exp.Message.StartsWith("Write to stream faild at"));
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                }
            }
        }

        private class Sample
        {
            public string Id { get; set; }
        }
    }
}