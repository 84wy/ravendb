import viewModelBase = require("viewmodels/viewModelBase");
import app = require("durandal/app");
import fileDownloader = require("common/fileDownloader");
import graphHelper = require("common/helpers/graph/graphHelper");
import d3 = require("d3");
import rbush = require("rbush");
import gapFinder = require("common/helpers/graph/gapFinder");
import generalUtils = require("common/generalUtils");
import rangeAggregator = require("common/helpers/graph/rangeAggregator");
import liveIndexPerformanceWebSocketClient = require("common/liveIndexPerformanceWebSocketClient");
import inProgressAnimator = require("common/helpers/graph/inProgressAnimator");

type rTreeLeaf = {
    minX: number;
    minY: number;
    maxX: number;
    maxY: number;
    actionType: "toggleIndex" | "trackItem" | "gapItem";
    arg: any;
}

interface IndexingPerformanceGap {
    DurationInMilliseconds: number;
    StartTime: string;     
}

class hitTest {
    cursor = ko.observable<string>("auto");
    private rTree = rbush<rTreeLeaf>();
    private container: d3.Selection<any>;
    private onToggleIndex: (indexName: string) => void;
    private handleTrackTooltip: (item: Raven.Client.Documents.Indexes.IndexingPerformanceOperation, x: number, y: number) => void;
    private handleGapTooltip: (item: IndexingPerformanceGap, x: number, y: number) => void;
    private removeTooltip: () => void;
   
    reset() {
        this.rTree.clear();
    }

    init(container: d3.Selection<any>,
        onToggleIndex: (indeName: string) => void,
        handleTrackTooltip: (item: Raven.Client.Documents.Indexes.IndexingPerformanceOperation, x: number, y: number) => void,
        handleGapTooltip: (item: IndexingPerformanceGap, x: number, y: number) => void,
        removeTooltip: () => void) {       
        this.container = container;
        this.onToggleIndex = onToggleIndex;
        this.handleTrackTooltip = handleTrackTooltip;
        this.handleGapTooltip = handleGapTooltip;
        this.removeTooltip = removeTooltip;        
    }

    registerTrackItem(x: number, y: number, width: number, height: number, element: Raven.Client.Documents.Indexes.IndexingPerformanceOperation) {
        const data = {
            minX: x,
            minY: y,
            maxX: x + width,
            maxY: y + height,
            actionType: "trackItem",
            arg: element
        } as rTreeLeaf;
        this.rTree.insert(data);
    }

    registerIndexToggle(x: number, y: number, width: number, height: number, indexName: string) {
        const data = {
            minX: x,
            minY: y,
            maxX: x + width,
            maxY: y + height,
            actionType: "toggleIndex",
            arg: indexName
        } as rTreeLeaf;
        this.rTree.insert(data);
    }

    registerGapItem(x: number, y: number, width: number, height: number, element: IndexingPerformanceGap) {
        const data = {
            minX: x,
            minY: y,
            maxX: x + width,
            maxY: y + height,
            actionType: "gapItem",
            arg: element
        } as rTreeLeaf;
        this.rTree.insert(data);
    }

    onClick() {
        const clickLocation = d3.mouse(this.container.node());

        if ((d3.event as any).defaultPrevented) {
            return;
        }

        const items = this.findItems(clickLocation[0], clickLocation[1]);

        for (let i = 0; i < items.length; i++) {
            const item = items[i];

            if (item.actionType === "toggleIndex") {
                 this.onToggleIndex(item.arg as string);
            } 
        }
    }

    onMouseMove() {
        const clickLocation = d3.mouse(this.container.node());
        const items = this.findItems(clickLocation[0], clickLocation[1]);              

        const currentItem = items.filter(x => x.actionType === "trackItem").map(x => x.arg as Raven.Client.Documents.Indexes.IndexingPerformanceOperation)[0];
        if (currentItem) {
            this.handleTrackTooltip(currentItem, clickLocation[0], clickLocation[1]);           
        }
        else {
            const currentGapItem = items.filter(x => x.actionType === "gapItem").map(x => x.arg as IndexingPerformanceGap)[0];
            if (currentGapItem) {
                this.handleGapTooltip(currentGapItem, clickLocation[0], clickLocation[1]);
            }
            else {
                this.removeTooltip();
            }
        }        
    }

    private findItems(x: number, y: number): Array<rTreeLeaf> {
        return this.rTree.search({
            minX: x,
            maxX: x,
            minY: y - metrics.brushSectionHeight,
            maxY: y - metrics.brushSectionHeight
        });
    }
}

class metrics extends viewModelBase {

    static readonly colors = {
        axis: "#546175",
        gaps: "#ca1c59",
        brushChartColor: "#37404b",
        brushChartStrokeColor: "#008cc9",
        trackBackground: "#2c343a",
        trackNameBg: "rgba(57, 67, 79, 0.8)",
        trackNameFg: "#98a7b7",
        openedTrackArrow: "#ca1c59",
        closedTrackArrow: "#98a7b7",
        collectionNameTextColor: "#2c343a",

        tracks: {
            "Collection": "#046293",
            "Indexing": "#607d8b",
            "Cleanup": "#1a858e",
            "References": "#ac2258",
            "Map": "#0b4971",
            "Storage/DocumentRead": "#0077b5",
            "Linq": "#008cc9",
            "LoadDocument": "#008cc9",
            "Bloom": "#34b3e4",
            "Lucene/Delete": "#66418c",
            "Lucene/AddDocument": "#8d6cab",
            "Lucene/Convert": "#7b539d",
            "CreateBlittableJson": "#313fa0",
            "Aggregation/BlittableJson": "#ec407a",
            "GetMapEntriesTree": "#689f39",
            "GetMapEntries": "#8cc34b",
            "Storage/RemoveMapResult": "#ff7000",
            "Storage/PutMapResult": "#fe8f01",
            "Reduce": "#98041b",
            "Tree": "#af1923",
            "Aggregation/Leafs": "#890e4f",
            "Aggregation/Branches": "#ad1457",
            "Storage/ReduceResults": "#e65100",
            "NestedValues": "#795549",
            "Storage/Read": "#faa926",
            "Aggregation/NestedValues": "#d81a60",
            "Lucene/FlushToDisk": "#a487ba",
            "Storage/Commit": "#5b912d",
            "Lucene/RecreateSearcher": "#b79ec7"
        }
    }

    static readonly brushSectionHeight = 40;
    private static readonly brushSectionIndexesWorkHeight = 22;
    private static readonly brushSectionLineWidth = 1;
    private static readonly trackHeight = 18; // height used for callstack item
    private static readonly stackPadding = 1; // space between call stacks
    private static readonly trackMargin = 4;
    private static readonly closedTrackPadding = 2;
    private static readonly openedTrackPadding = 4;
    private static readonly axisHeight = 35; 
    private static readonly inProgressStripesPadding = 7;

    private static readonly maxRecursion = 5;
    private static readonly minGapSize = 10 * 1000; // 10 seconds

    private data: Raven.Client.Documents.Indexes.IndexPerformanceStats[] = [];
    private totalWidth: number;
    private totalHeight: number;

    private searchText = ko.observable<string>();

    private liveViewClient = ko.observable<liveIndexPerformanceWebSocketClient>();
    private autoScroll = ko.observable<boolean>(false);

    private indexNames = ko.observableArray<string>();
    private filteredIndexNames = ko.observableArray<string>();
    private expandedTracks = ko.observableArray<string>();
    private isImport = ko.observable<boolean>(false);
    private importFileName = ko.observable<string>();

    private xTickFormat = d3.time.format("%H:%M:%S");
    private canvas: d3.Selection<any>;
    private inProgressCanvas: d3.Selection<any>;
    private svg: d3.Selection<any>; // spans to canvas size (to provide brush + zoom/pan features)
    private brush: d3.svg.Brush<number>;
    private brushAndZoomCallbacksDisabled = false;
    private xBrushNumericScale: d3.scale.Linear<number, number>;
    private xBrushTimeScale: d3.time.Scale<number, number>;
    private yBrushValueScale: d3.scale.Linear<number, number>;
    private xNumericScale: d3.scale.Linear<number, number>;
    private brushSection: HTMLCanvasElement; // virtual canvas for brush section
    private brushContainer: d3.Selection<any>;
    private zoom: d3.behavior.Zoom<any>;
    private yScale: d3.scale.Ordinal<string, number>;
    private currentYOffset = 0;
    private maxYOffset = 0;
    private hitTest = new hitTest();
    private tooltip: d3.Selection<Raven.Client.Documents.Indexes.IndexingPerformanceOperation | IndexingPerformanceGap>;   

    private inProgressAnimator: inProgressAnimator;
    private inProgressMarkerCanvas: HTMLCanvasElement;

    private gapFinder: gapFinder;

    private dialogVisible = false;
    private canExpandAll: KnockoutComputed<boolean>;

    hasAnyData = ko.observable<boolean>(false);

    private static readonly openedTrackHeight = metrics.openedTrackPadding
        + (metrics.maxRecursion + 1) * metrics.trackHeight
        + metrics.maxRecursion * metrics.stackPadding
        + metrics.openedTrackPadding;

    private static readonly closedTrackHeight = metrics.closedTrackPadding
        + metrics.trackHeight
        + metrics.closedTrackPadding;

    constructor() {
        super();

        this.canExpandAll = ko.pureComputed(() => {
            const indexNames = this.indexNames();
            const expandedTracks = this.expandedTracks();

            return indexNames.length && indexNames.length !== expandedTracks.length;
        });

        this.searchText.throttle(200).subscribe(() => this.filterIndexes());

        this.autoScroll.subscribe(v => {
            if (v) {
                this.scrollToRight();
            } else {
                // cancel transition (if any)
                this.brushContainer
                    .transition(); 
            }
        });
    }

    activate(args: { indexName: string, database: string}): void {
        super.activate(args);

        if (args.indexName) {
            this.expandedTracks.push(args.indexName);
        }
    }

    deactivate() {
        super.deactivate();

        if (this.liveViewClient()) {
            this.cancelLiveView();
        }
    }

    compositionComplete() {
        super.compositionComplete();

        this.tooltip = d3.select(".tooltip");

        [this.totalWidth, this.totalHeight] = this.getPageHostDimenensions();
        this.totalWidth -= 1;

        this.totalHeight -= 50; // substract toolbar height

        this.initCanvases();

        this.hitTest.init(this.svg,
            (indexName) => this.onToggleIndex(indexName),          
            (item, x, y) => this.handleTrackTooltip(item, x, y),
            (gapItem, x, y) => this.handleGapTooltip(gapItem, x, y),
            () => this.hideTooltip());

        this.enableLiveView();
    }

    private initCanvases() {
        const metricsContainer = d3.select("#metricsContainer");
        this.canvas = metricsContainer
            .append("canvas")
            .attr("width", this.totalWidth + 1)
            .attr("height", this.totalHeight);

        this.inProgressCanvas = metricsContainer
            .append("canvas")
            .attr("width", this.totalWidth + 1)
            .attr("height", this.totalHeight - metrics.brushSectionHeight)
            .style("top", (metrics.brushSectionHeight + metrics.axisHeight) + "px");

        const inProgressCanvasNode = this.inProgressCanvas.node() as HTMLCanvasElement;
        const inProgressContext = inProgressCanvasNode.getContext("2d");
        inProgressContext.translate(0, -metrics.axisHeight);

        this.inProgressAnimator = new inProgressAnimator(inProgressCanvasNode);

        this.svg = metricsContainer
            .append("svg")
            .attr("width", this.totalWidth + 1)
            .attr("height", this.totalHeight);

        this.xBrushNumericScale = d3.scale.linear<number>()
            .range([0, this.totalWidth])
            .domain([0, this.totalWidth]);

        this.xNumericScale = d3.scale.linear<number>()
            .range([0, this.totalWidth])
            .domain([0, this.totalWidth]);

        this.brush = d3.svg.brush()
            .x(this.xBrushNumericScale)
            .on("brush", () => this.onBrush());

        this.zoom = d3.behavior.zoom()
            .x(this.xNumericScale)
            .on("zoom", () => this.onZoom());

        this.svg
            .append("svg:rect")
            .attr("class", "pane")
            .attr("width", this.totalWidth)
            .attr("height", this.totalHeight - metrics.brushSectionHeight)
            .attr("transform", "translate(" + 0 + "," + metrics.brushSectionHeight + ")")
            .call(this.zoom)
            .call(d => this.setupEvents(d));
    }

    private setupEvents(selection: d3.Selection<any>) {
        let mousePressed = false;

        const onMove = () => {
            this.hitTest.onMouseMove();
        }

        this.hitTest.cursor.subscribe((cursor) => {
            selection.style("cursor", cursor);
        });

        selection.on("mousemove.tip", onMove);

        selection.on("click", () => this.hitTest.onClick());

        selection
            .on("mousedown.tip", () => selection.on("mousemove.tip", null))
            .on("mouseup.tip", () => selection.on("mousemove.tip", onMove));

        selection
            .on("mousedown.live", () => {
                if (this.liveViewClient()) {
                    this.liveViewClient().pauseUpdates();
                }
            });
        selection
            .on("mouseup.live", () => {
                if (this.liveViewClient()) {
                    this.liveViewClient().resumeUpdates();
                }
            });

        selection
            .on("mousedown.yShift", () => {
                const node = selection.node();
                const initialClickLocation = d3.mouse(node);
                const initialOffset = this.currentYOffset;

                selection.on("mousemove.yShift", () => {
                    const currentMouseLocation = d3.mouse(node);
                    const yDiff = currentMouseLocation[1] - initialClickLocation[1];

                    const newYOffset = initialOffset - yDiff;

                    this.currentYOffset = newYOffset;
                    this.fixCurrentOffset();
                });

                selection.on("mouseup.yShift", () => selection.on("mousemove.yShift", null));
            });

        selection.on("dblclick.zoom", null);
    }

    private filterIndexes() {
        const criteria = this.searchText().toLowerCase();

        this.filteredIndexNames(this.indexNames().filter(x => x.toLowerCase().includes(criteria)));

        this.drawMainSection();
    }

    private enableLiveView() {
        let firstTime = true;

        const onDataUpdate = (data: Raven.Client.Documents.Indexes.IndexPerformanceStats[]) => {
            let timeRange: [Date, Date];
            if (!firstTime) {
                const timeToRemap = this.brush.empty() ? this.xBrushNumericScale.domain() as [number, number] : this.brush.extent() as [number, number];
                timeRange = timeToRemap.map(x => this.xBrushTimeScale.invert(x));
            }

            this.data = data;

            const [workData, maxConcurrentIndexes] = this.prepareTimeData();

            if (!firstTime) {
                const newBrush: [number, number] = timeRange.map(x => this.xBrushTimeScale(x));
                this.setZoomAndBrush(newBrush, brush => brush.extent(newBrush));
            }

            if (this.autoScroll()) {
                this.scrollToRight();
            }

            this.draw(workData, maxConcurrentIndexes, firstTime);

            if (firstTime) {
                firstTime = false;
            }
        };

        this.liveViewClient(new liveIndexPerformanceWebSocketClient(this.activeDatabase(), onDataUpdate));
    }

    scrollToRight() {
        const currentExtent = this.brush.extent() as [number, number];
        const extentWidth = currentExtent[1] - currentExtent[0];

        const existingBrushStart = currentExtent[0];

        if (currentExtent[1] < this.totalWidth) {

            const rightPadding = 100;
            const desiredShift = rightPadding * extentWidth / this.totalWidth;

            const desiredExtentStart = this.totalWidth + desiredShift - extentWidth;

            const moveFunc = (startX: number) => {
                this.brush.extent([startX, startX + extentWidth]);
                this.brushContainer.call(this.brush);

                this.onBrush();
            };

            this.brushContainer
                .transition()
                .duration(500)
                .tween("side-effect", () => {
                    const interpolator = d3.interpolate(existingBrushStart, desiredExtentStart);

                    return (t) => {
                        const currentStart = interpolator(t);
                        moveFunc(currentStart);
                    }
                });
        }
    }

    private cancelLiveView() {
        this.liveViewClient().dispose();
        this.liveViewClient(null);
    }

    private draw(workData: indexesWorkData[], maxConcurrentIndexes: number, resetFilteredIndexNames: boolean) {
        this.hasAnyData(this.data.length > 0);

        this.prepareBrushSection(workData, maxConcurrentIndexes);
        this.prepareMainSection(resetFilteredIndexNames);

        const canvas = this.canvas.node() as HTMLCanvasElement;
        const context = canvas.getContext("2d");

        context.clearRect(0, 0, this.totalWidth, metrics.brushSectionHeight);
        context.drawImage(this.brushSection, 0, 0);
        this.drawMainSection();
    }

    private prepareTimeData(): [indexesWorkData[], number] {
        let timeRanges = this.extractTimeRanges(); 

        let maxConcurrentIndexes: number;
        let workData: indexesWorkData[];

        if (timeRanges.length === 0) {
            // no data - create fake scale
            timeRanges = [[new Date(), new Date()]];
            maxConcurrentIndexes = 1;
            workData = [];
        } else {
            const aggregatedRanges = new rangeAggregator(timeRanges);
            workData = aggregatedRanges.aggregate();
            maxConcurrentIndexes = aggregatedRanges.maxConcurrentIndexes;
        }

        this.gapFinder = new gapFinder(timeRanges, metrics.minGapSize);
        this.xBrushTimeScale = this.gapFinder.createScale(this.totalWidth, 0);

        return [workData, maxConcurrentIndexes];
    }

    private prepareBrushSection(workData: indexesWorkData[], maxConcurrentIndexes: number) {
        this.brushSection = document.createElement("canvas");
        this.brushSection.width = this.totalWidth + 1;
        this.brushSection.height = metrics.brushSectionHeight;

        this.yBrushValueScale = d3.scale.linear()
            .domain([0, maxConcurrentIndexes])
            .range([0, metrics.brushSectionIndexesWorkHeight]); 

        const context = this.brushSection.getContext("2d");
        this.drawXaxis(context, this.xBrushTimeScale, metrics.brushSectionHeight);

        context.strokeStyle = metrics.colors.axis;
        context.strokeRect(0.5, 0.5, this.totalWidth, metrics.brushSectionHeight - 1);

        context.fillStyle = metrics.colors.brushChartColor;  
        context.strokeStyle = metrics.colors.brushChartStrokeColor; 
        context.lineWidth = metrics.brushSectionLineWidth;

        // Draw area chart showing indexes work
        let x1: number, x2: number, y0: number = 0, y1: number;
        for (let i = 0; i < workData.length - 1; i++) {

            context.beginPath();
            x1 = this.xBrushTimeScale(new Date(workData[i].pointInTime));
            y1 = Math.round(this.yBrushValueScale(workData[i].numberOfIndexesWorking)) + 0.5;
            x2 = this.xBrushTimeScale(new Date(workData[i + 1].pointInTime));
            context.moveTo(x1, metrics.brushSectionHeight - y0);
            context.lineTo(x1, metrics.brushSectionHeight - y1);

            // Don't want to draw line -or- rect at level 0
            if (y1 !== 0) {
                context.lineTo(x2, metrics.brushSectionHeight - y1);
                context.fillRect(x1, metrics.brushSectionHeight - y1, x2-x1, y1);
            } 

            context.stroke();
            y0 = y1; 
        }

        // Draw last line:
        context.beginPath();
        context.moveTo(x2, metrics.brushSectionHeight - y1);
        context.lineTo(x2, metrics.brushSectionHeight);
        context.stroke(); 

        this.drawBrushGaps(context);
        this.prepareBrush();
    }

    private drawBrushGaps(context: CanvasRenderingContext2D) {
        for (let i = 0; i < this.gapFinder.gapsPositions.length; i++) {
            const gap = this.gapFinder.gapsPositions[i];

            context.strokeStyle = metrics.colors.gaps;

            const gapX = this.xBrushTimeScale(gap.start);
            context.moveTo(gapX, 1);
            context.lineTo(gapX, metrics.brushSectionHeight - 2);
            context.stroke();
        }
    }

    private prepareBrush() {
        const hasBrush = !!this.svg.select("g.brush").node();

        if (!hasBrush) {
            this.brushContainer = this.svg
                .append("g")
                .attr("class", "x brush");

            this.brushContainer
                .call(this.brush)
                .selectAll("rect")
                .attr("y", 0)
                .attr("height", metrics.brushSectionHeight - 1);
        }
    }

    private prepareMainSection(resetFilteredIndexNames: boolean) {
        this.findAndSetIndexNames();
        if (resetFilteredIndexNames) {
            this.filteredIndexNames(this.indexNames());
        }
    }

    private findAndSetIndexNames() {
        this.indexNames(_.uniq(this.data.map(x => x.IndexName)));
    }

    private fixCurrentOffset() {
        this.currentYOffset = Math.min(Math.max(0, this.currentYOffset), this.maxYOffset);
    }

    private constructYScale() {
        let currentOffset = metrics.axisHeight - this.currentYOffset;
        let domain = [] as Array<string>;
        let range = [] as Array<number>;

        const indexesInfo = this.filteredIndexNames();

        for (let i = 0; i < indexesInfo.length; i++) {
            const indexName = indexesInfo[i];

            domain.push(indexName);
            range.push(currentOffset);

            const isOpened = _.includes(this.expandedTracks(), indexName);

            const itemHeight = isOpened ? metrics.openedTrackHeight : metrics.closedTrackHeight;

            currentOffset += itemHeight + metrics.trackMargin;
        }

        this.yScale = d3.scale.ordinal<string, number>()
            .domain(domain)
            .range(range);
    }

    private calcMaxYOffset() {
        const expandedTracksCount = this.expandedTracks().length;
        const closedTracksCount = this.filteredIndexNames().length - expandedTracksCount;

        const offset = metrics.axisHeight
            + this.filteredIndexNames().length * metrics.trackMargin
            + expandedTracksCount * metrics.openedTrackHeight
            + closedTracksCount * metrics.closedTrackHeight;

        const availableHeightForTracks = this.totalHeight - metrics.brushSectionHeight;

        const extraBottomMargin = 100;

        this.maxYOffset = Math.max(offset + extraBottomMargin - availableHeightForTracks, 0);
    }

    private drawXaxis(context: CanvasRenderingContext2D, scale: d3.time.Scale<number, number>, height: number) {
        context.save();

        const step = 200;
        const initialOffset = 100;

        const ticks = d3.range(initialOffset, this.totalWidth - step, step)
            .map(y => scale.invert(y));

        context.strokeStyle = metrics.colors.axis;
        context.fillStyle = metrics.colors.axis;

        context.beginPath();
        context.setLineDash([4, 2]);

        ticks.forEach((x, i) => {
            context.moveTo(initialOffset + (i * step) + 0.5, 0);
            context.lineTo(initialOffset + (i * step) + 0.5, height);
        });
        context.stroke();

        context.beginPath();

        context.textAlign = "left";
        context.textBaseline = "top";
        context.font = "10px Lato";
        ticks.forEach((x, i) => {
            // draw text with 5px left padding
            context.fillText(this.xTickFormat(x), initialOffset + (i * step) + 5, 5);
        });
        context.restore();
    }

    private onZoom() {
        this.autoScroll(false);
        if (!this.brushAndZoomCallbacksDisabled) {
            this.brush.extent(this.xNumericScale.domain() as [number, number]);
            this.brushContainer
                .call(this.brush);

            this.drawMainSection();
        }
    }

    private onBrush() {
        if (!this.brushAndZoomCallbacksDisabled) {
            this.xNumericScale.domain((this.brush.empty() ? this.xBrushNumericScale.domain() : this.brush.extent()) as [number, number]);
            this.zoom.x(this.xNumericScale);
            this.drawMainSection();
        }
    }

    private extractTimeRanges(): Array<[Date, Date]> {
        const result = [] as Array<[Date, Date]>;
        this.data.forEach(indexStats => {
            indexStats.Performance.forEach(perfStat => {
                const perfStatsWithCache = perfStat as IndexingPerformanceStatsWithCache;
                const start = perfStatsWithCache.StartedAsDate;
                let end: Date;
                if (perfStat.Completed) {
                    end = perfStatsWithCache.CompletedAsDate;
                } else {
                    end = new Date(start.getTime() + perfStat.DurationInMilliseconds);
                }
                result.push([start, end]);
            });
        });

        return result;
    }

    private drawMainSection() {
        this.inProgressAnimator.reset();
        this.hitTest.reset();
        this.calcMaxYOffset();
        this.fixCurrentOffset();
        this.constructYScale();

        const visibleTimeFrame = this.xNumericScale.domain().map(x => this.xBrushTimeScale.invert(x)) as [Date, Date];

        const xScale = this.gapFinder.trimmedScale(visibleTimeFrame, this.totalWidth, 0);

        const canvas = this.canvas.node() as HTMLCanvasElement;
        const context = canvas.getContext("2d");

        context.save();
        try {
            context.translate(0, metrics.brushSectionHeight);
            context.clearRect(0, 0, this.totalWidth, this.totalHeight - metrics.brushSectionHeight);

            this.drawTracksBackground(context, xScale);

            if (xScale.domain().length) {
                this.drawXaxis(context, xScale, this.totalHeight);
            }

            context.save();
            try {
                context.beginPath();
                context.rect(0, metrics.axisHeight, this.totalWidth, this.totalHeight - metrics.brushSectionHeight);
                context.clip();

                this.drawTracks(context, xScale, visibleTimeFrame);
                this.drawIndexNames(context);
                this.drawGaps(context, xScale);
            } finally {
                context.restore();
            }
        } finally {
            context.restore();
        }

        this.inProgressAnimator.animate();
    }

    private drawTracksBackground(context: CanvasRenderingContext2D, xScale: d3.time.Scale<number, number>) {
        context.save();

        context.beginPath();
        context.rect(0, metrics.axisHeight, this.totalWidth, this.totalHeight - metrics.brushSectionHeight);
        context.clip();

        this.data.forEach(perfStat => {
            const yStart = this.yScale(perfStat.IndexName);

            const isOpened = _.includes(this.expandedTracks(), perfStat.IndexName);

            context.fillStyle = metrics.colors.trackBackground;
            context.fillRect(0, yStart, this.totalWidth, isOpened ? metrics.openedTrackHeight : metrics.closedTrackHeight);
        });

        context.restore();
    }

    private drawTracks(context: CanvasRenderingContext2D, xScale: d3.time.Scale<number, number>, visibleTimeFrame: [Date, Date]) {
        if (xScale.domain().length === 0) {
            return;
        }

        const visibleStartDateAsInt = visibleTimeFrame[0].getTime();
        const visibleEndDateAsInt = visibleTimeFrame[1].getTime();

        const extentFunc = gapFinder.extentGeneratorForScaleWithGaps(xScale);

        this.data.forEach(perfStat => {
            const isOpened = _.includes(this.expandedTracks(), perfStat.IndexName);
            let yStart = this.yScale(perfStat.IndexName);
            yStart += isOpened ? metrics.openedTrackPadding : metrics.closedTrackPadding;

            const performance = perfStat.Performance;
            const perfLength = performance.length;
            for (let perfIdx = 0; perfIdx < perfLength; perfIdx++) {
                const perf = performance[perfIdx];
                const startDate = (perf as IndexingPerformanceStatsWithCache).StartedAsDate;
                const x1 = xScale(startDate);

                const startDateAsInt = startDate.getTime();

                const endDateAsInt = startDateAsInt + perf.DurationInMilliseconds;
                if (endDateAsInt < visibleStartDateAsInt || visibleEndDateAsInt < startDateAsInt)
                    continue;

                const yOffset = isOpened ? metrics.trackHeight + metrics.stackPadding : 0;
                this.drawStripes(context, [perf.Details], x1, yStart + (isOpened ? yOffset : 0), yOffset, extentFunc);

                if (!perf.Completed) {
                    this.findInProgressAction(context, perf, extentFunc, x1, yStart + (isOpened ? yOffset : 0), yOffset);
                }
            }
        });
    }

    private findInProgressAction(context: CanvasRenderingContext2D, perf: Raven.Client.Documents.Indexes.IndexingPerformanceStats, extentFunc: (duration: number) => number,
        xStart: number, yStart: number, yOffset: number): void {

        const extractor = (perfs: Raven.Client.Documents.Indexes.IndexingPerformanceOperation[], xStart: number, yStart: number, yOffset: number) => {

            let currentX = xStart;

            perfs.forEach(op => {
                const dx = extentFunc(op.DurationInMilliseconds);

                this.inProgressAnimator.register([currentX, yStart, dx, metrics.trackHeight]);

                if (op.Operations.length > 0) {
                    extractor(op.Operations, currentX, yStart + yOffset, yOffset);
                }
                currentX += dx;
            });
        }

        extractor([perf.Details], xStart, yStart, yOffset);
    }

    private getColorForOperation(operationName: string): string {
        const { tracks } = metrics.colors;
        if (operationName in tracks) {
            return (tracks as dictionary<string>)[operationName];
        }

        if (operationName.startsWith("Collection_")) {
            return tracks.Collection;
        }

        throw new Error("Unable to find color for: " + operationName);
    }

    private drawStripes(context: CanvasRenderingContext2D, operations: Array<Raven.Client.Documents.Indexes.IndexingPerformanceOperation>, xStart: number, yStart: number,
        yOffset: number, extentFunc: (duration: number) => number) {

        let currentX = xStart;
        const length = operations.length;
        for (let i = 0; i < length; i++) {
            const op = operations[i];
            context.fillStyle = this.getColorForOperation(op.Name);

            const dx = extentFunc(op.DurationInMilliseconds);

            context.fillRect(currentX, yStart, dx, metrics.trackHeight);

            if (yOffset !== 0) { // track is opened
                if (dx >= 0.8) { // don't show tooltip for very small items
                    this.hitTest.registerTrackItem(currentX, yStart, dx, metrics.trackHeight, op);
                }
                if (op.Name.startsWith("Collection_")) {
                    context.fillStyle = metrics.colors.collectionNameTextColor;
                    const text = op.Name.substr("Collection_".length);
                    const textWidth = context.measureText(text).width
                    const truncatedText = graphHelper.truncText(text, textWidth, dx - 4);
                    if (truncatedText) {
                        context.font = "12px Lato";
                        context.fillText(truncatedText, currentX + 2, yStart + 13, dx - 4);
                    }
                }
            }
            
            if (op.Operations.length > 0) {
                this.drawStripes(context, op.Operations, currentX, yStart + yOffset, yOffset, extentFunc);
            }
            currentX += dx;
        }
    }

    private drawIndexNames(context: CanvasRenderingContext2D) {
        const yScale = this.yScale;
        const textShift = 14.5;
        const textStart = 3 + 8 + 4;

        this.filteredIndexNames().forEach((indexName) => {
            context.font = "12px Lato";
            const rectWidth = context.measureText(indexName).width + 2 * 3 /* left right padding */ + 8 /* arrow space */ + 4; /* padding between arrow and text */ 

            context.fillStyle = metrics.colors.trackNameBg;
            context.fillRect(2, yScale(indexName) + metrics.closedTrackPadding, rectWidth, metrics.trackHeight);
            this.hitTest.registerIndexToggle(2, yScale(indexName), rectWidth, metrics.
                trackHeight, indexName);
            context.fillStyle = metrics.colors.trackNameFg;
            context.fillText(indexName, textStart + 0.5, yScale(indexName) + textShift);

            const isOpened = _.includes(this.expandedTracks(), indexName);
            context.fillStyle = isOpened ? metrics.colors.openedTrackArrow : metrics.colors.closedTrackArrow;
            graphHelper.drawArrow(context, 5, yScale(indexName) + 6, !isOpened);
        });
    }

    private drawGaps(context: CanvasRenderingContext2D, xScale: d3.time.Scale<number, number>) {      

        const range = xScale.range();
        context.strokeStyle = metrics.colors.gaps;

        for (let i = 1; i < range.length; i += 2) { 
            const gapX = Math.floor(range[i]) + 0.5;

            context.beginPath();
            context.moveTo(gapX, metrics.axisHeight);
            context.lineTo(gapX, this.totalHeight);
            context.stroke();

            const indexToGapFinderPosition = (i - 1) / 2; 
            const gapInfo = this.gapFinder.gapsPositions[indexToGapFinderPosition];
            if (gapInfo) {
                this.hitTest.registerGapItem(gapX - 5, metrics.axisHeight, 10, this.totalHeight,
                    { DurationInMilliseconds: gapInfo.durationInMillis, StartTime: gapInfo.start.toLocaleTimeString() });
            }
        }
    }

    private onToggleIndex(indexName: string) {
        if (_.includes(this.expandedTracks(), indexName)) {
            this.expandedTracks.remove(indexName);
        } else {
            this.expandedTracks.push(indexName);
        }

        this.drawMainSection();
    }

    expandAll() {
        this.expandedTracks(this.indexNames().slice());
        this.drawMainSection();
    }

    collapseAll() {
        this.expandedTracks([]);
        this.drawMainSection();
    }

    private handleGapTooltip(element: IndexingPerformanceGap, x: number, y: number) {
        const currentDatum = this.tooltip.datum();

        if (currentDatum !== element) {
            const tooltipHtml = "Gap start time: " + (element).StartTime +
                                  "<br />Gap duration: " + generalUtils.formatMillis((element).DurationInMilliseconds);       
            this.handleTooltip(element, x, y, tooltipHtml);
        }
    } 

    private handleTrackTooltip(element: Raven.Client.Documents.Indexes.IndexingPerformanceOperation, x: number, y: number) {
        const currentDatum = this.tooltip.datum();

        if (currentDatum !== element) {
            let tooltipHtml = `${element.Name}<br/>Duration: ${generalUtils.formatMillis((element).DurationInMilliseconds)}`;

            if (element.CommitDetails) {   
                let commitDetails: string;
                commitDetails = `<br/>*** Commit details ***<br/>`;
                commitDetails += `Modified pages: ${element.CommitDetails.NumberOfModifiedPages.toLocaleString()}<br/>`;
                commitDetails += `Pages written to disk: ${element.CommitDetails.NumberOfPagesWrittenToDisk.toLocaleString()}`;
                tooltipHtml += commitDetails;
            }
            if (element.MapDetails) {
                let mapDetails: string;
                mapDetails = `<br/>*** Map details ***<br/>`;
                mapDetails += `Allocation budget: ${generalUtils.formatBytesToSize(element.MapDetails.AllocationBudget)}<br/>`;
                mapDetails += `Batch status: ${element.MapDetails.BatchCompleteReason || 'In progress'}<br/>`;
                mapDetails += `Currently allocated: ${generalUtils.formatBytesToSize(element.MapDetails.CurrentlyAllocated)} <br/>`;
                mapDetails += `Process private memory: ${generalUtils.formatBytesToSize(element.MapDetails.ProcessPrivateMemory)}<br/>`;
                mapDetails += `Process working set: ${generalUtils.formatBytesToSize(element.MapDetails.ProcessWorkingSet)}`;
                tooltipHtml += mapDetails;
            }
            if (element.ReduceDetails) {
                let reduceDetails: string;
                reduceDetails = `<br/>*** Reduce details ***<br/>`;
                reduceDetails += `Compressed leaves: ${element.ReduceDetails.NumberOfCompressedLeafs.toLocaleString()}<br/>`;
                reduceDetails += `Modified branches: ${element.ReduceDetails.NumberOfModifiedBranches.toLocaleString()}<br/>`;
                reduceDetails += `Modified leaves: ${element.ReduceDetails.NumberOfModifiedLeafs.toLocaleString()}`;
                tooltipHtml += reduceDetails;
            }           

            this.handleTooltip(element, x, y, tooltipHtml);
        }
    }

    private handleTooltip(element: Raven.Client.Documents.Indexes.IndexingPerformanceOperation | IndexingPerformanceGap, x: number, y: number, tooltipHtml: string) {
        if (element && !this.dialogVisible) {
            const tooltipWidth = $("#indexingPerformance .tooltip").width() + 30;
            x = Math.min(x, Math.max(this.totalWidth - tooltipWidth, 0));

            this.tooltip.transition()
                .duration(200)                                                          
                .style("opacity", 1)              
                .style("left", (x + 10) + "px")
                .style("top", (y + 10) + "px");

            this.tooltip
                .html(tooltipHtml)
                .datum(element);

        } else {
            this.hideTooltip();
        }
    }    

    private hideTooltip() {
        this.tooltip.transition()
            .duration(200)
            .style("opacity", 0);
         
        this.tooltip.datum(null);      
    }

    fileSelected() { 
        const fileInput = <HTMLInputElement>document.querySelector("#importFilePicker");
        const self = this;
        if (fileInput.files.length === 0) {
            return;
        }

        const file = fileInput.files[0];
        const reader = new FileReader();
        reader.onload = function() {
// ReSharper disable once SuspiciousThisUsage
            self.dataImported(this.result);
        };
        reader.onerror = function(error: any) {
            alert(error);
        };
        reader.readAsText(file);

        this.importFileName(fileInput.files[0].name);

        // Must clear the filePicker element value so that user will be able to import the -same- file after closing the imported view...
        const $input = $("#importFilePicker");
        $input.val(null);
    }

    private dataImported(result: string) {
        this.cancelLiveView();

        this.data = JSON.parse(result);
        this.fillCache();
        this.resetGraphData();
        const [workData, maxConcurrentIndexes] = this.prepareTimeData();
        this.draw(workData, maxConcurrentIndexes, true);
        this.isImport(true);
    }

    private fillCache() {
        const isoParser = d3.time.format.iso;

        this.data.forEach(indexStats => {
            indexStats.Performance.forEach(perfStat => {
                const withCache = perfStat as IndexingPerformanceStatsWithCache;
                withCache.StartedAsDate = isoParser.parse(withCache.Started);
                withCache.CompletedAsDate = withCache.Completed ? isoParser.parse(withCache.Completed) : undefined;
            });
        });
    }

    closeImport() {
        this.isImport(false);
        this.resetGraphData();
        this.enableLiveView();
    }

    private resetGraphData() {
        this.setZoomAndBrush([0, this.totalWidth], brush => brush.clear());

        this.expandedTracks([]);
        this.searchText("");
    }

    private setZoomAndBrush(scale: [number, number], brushAction: (brush: d3.svg.Brush<any>) => void) {
        this.brushAndZoomCallbacksDisabled = true;

        this.xNumericScale.domain(scale);
        this.zoom.x(this.xNumericScale);

        brushAction(this.brush);
        this.brushContainer.call(this.brush);

        this.brushAndZoomCallbacksDisabled = false;
    }

    exportAsJson() {  
        let exportFileName;

        if (this.isImport()) {           
            exportFileName = this.importFileName().substring(0, this.importFileName().lastIndexOf('.'));                    
        } else {
            exportFileName = `indexPerf of ${this.activeDatabase().name} ${moment().format("YYYY-MM-DD HH-mm")}`; 
        }

        const keysToIgnore: Array<keyof IndexingPerformanceStatsWithCache> = ["StartedAsDate", "CompletedAsDate"];
        fileDownloader.downloadAsJson(this.data, exportFileName + ".json", exportFileName, (key, value) => {
            if (_.includes(keysToIgnore, key)) {
                return undefined;
            }
            return value;
        });
    }

}

export = metrics; 
 
