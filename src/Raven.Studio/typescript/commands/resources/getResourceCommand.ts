import commandBase = require("commands/commandBase");
import endpoints = require("endpoints");

class getResourceCommand extends commandBase {

    constructor(private resourceType: string, private resourceName: string) {
        super();
    }

    execute(): JQueryPromise<Raven.Client.Server.Operations.ResourceInfo> {
        const url = endpoints.global.resources.resource;
        const args = {
            type: this.resourceType,
            name: this.resourceName
        };

        return this.query<Raven.Client.Server.Operations.ResourceInfo>(url, args);
    }
}

export = getResourceCommand;
