using FastTests;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Facets;
using Xunit;

namespace SlowTests.MailingList
{
    public class TomCabanski : RavenNewTestBase
    {
        [Fact]
        public void CanEscapeGetFacets()
        {
            using (var store = GetDocumentStore())
            {
                store.Admin.Send(new PutIndexesOperation(new[] {new IndexDefinition
                {
                    Name ="test",
                    Maps = { "from doc in docs.Users select new { doc.Age, doc.IsActive, doc.BookVendor }" }
                }}));

                using (var s = store.OpenSession())
                {
                    s.Store(new FacetSetup
                    {
                        Id = "facets/test",
                        Facets =
                            {
                                new Facet
                                {
                                    Mode = FacetMode.Default,
                                    Name = "Age"
                                }
                            }
                    });
                    s.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    session.Advanced.MultiFacetedSearch(new FacetQuery(store.Conventions)
                    {
                        IndexName = "test",
                        Query = "(IsActive:true)  AND (BookVendor:\"stroheim & romann\")",
                        FacetSetupDoc = "facets/test"
                    });
                }
            }
        }
    }
}
