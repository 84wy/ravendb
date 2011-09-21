using Raven.Studio.Infrastructure;
using Raven.Studio.Models;

namespace Raven.Studio.Features.Documents
{
	public class EditDocumentModelLocator
	{
		public Observable<EditableDocumentModel> Current
		{
			get
			{
				var observable = new Observable<EditableDocumentModel>();
				var asyncDatabaseCommands = ApplicationModel.Current.Server.Value.SelectedDatabase.Value.AsyncDatabaseCommands;
				var docId = ApplicationModel.Current.GetQueryParam("id");

				asyncDatabaseCommands.GetAsync(docId)
					.ContinueOnSuccess(document =>
					                   observable.Value = new EditableDocumentModel(document, asyncDatabaseCommands)
					)
					.Catch();
				return observable;
			}
		}
	}
}