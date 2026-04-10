// Calculates the minimum width that would be available to the web UI on this device
function calculateMinimumAvailableWidth(urlParams) {
    var availableWidth, availableHeight,
        deviceSupportsRotation = true,
        screenWidthInUrl = urlParams.match(/screenwidth=(\d+)(&|$)/i),
        screenHeightInUrl = urlParams.match(/screenheight=(\d+)(&|$)/i);

    if (screenWidthInUrl && screenHeightInUrl) {
        // Screen dimensions have been provided by the native client in the URL - use them
        availableWidth = screenWidthInUrl[1];
        availableHeight = screenHeightInUrl[1];
    } else if (navigator.userAgent.match(/(Android)\s+([\d.]+)/) || navigator.userAgent.match(/(Windows Phone)/i)) {
        // Android and Windows Phone give inconsistent screen dimensions - either physical pixels or CSS pixels depending on the device
        // We can attempt to work out which dimensions we've got by comparing with window size
        var screenWidth = window.screen.width,
            screenHeight = window.screen.height,
            windowWidth = document.documentElement.clientWidth,
            windowHeight = document.documentElement.clientHeight,
            pixelRatio = window.devicePixelRatio;

        if (screenWidth == windowWidth) {
            // Screen dimensions seem to be provided in CSS pixels - use them
            availableWidth = screenWidth;
            availableHeight = screenHeight;
        } else if (screenWidth == (windowWidth * pixelRatio)) {
            // Screen dimensions seem to be provided in physical pixels - convert to CSS pixels
            availableWidth = Math.floor(screenWidth / pixelRatio);
            availableHeight = Math.floor(screenHeight / pixelRatio);
        } else {
            // No discernable relationship between screen and window dimensions - fall back to using window dimensions
            availableWidth = windowWidth;
            availableHeight = windowHeight;
        }
    } else if (navigator.userAgent.match(/(iPad|iPhone|iPod)/i)) {
        // For iOS devices we assume that the web page could stretch to the full width of the screen
        availableWidth = window.screen.width;
        availableHeight = window.screen.height;
    } else {
        deviceSupportsRotation = false; // We assume that all other devices are desktops, and therefore that they are not capable of being rotated.
        availableWidth = window.screen.availWidth;
        availableHeight = window.screen.availHeight;
    }

    // Special overrides that a client may provide
    if ((urlParams.indexOf("-norotate") != -1) || (urlParams.indexOf("-desktop") != -1)) {
        deviceSupportsRotation = false;
    }

    // If rotation is possible then consider both dimensions, otherwise (e.g. for desktops) just look at the width
    return deviceSupportsRotation ? Math.min(availableWidth, availableHeight) : availableWidth;
}

var uisize,
    query = window.location.search + window.location.hash,
    MEDIUM_UI_MINIMUM_WIDTH = 600,
    LARGE_UI_MINIMUM_WIDTH = 768; // Also defined in CSS to set minimum width of the page

// Note window.minimumAvailableWidth, window.useSmallUI and window.useSmallTiles are referenced in ctxs.ui.js

if (query.indexOf('-ph') != -1) {
    window.minimumAvailableWidth = 100; // act as if on very small device
} else if (query.indexOf('-med') != -1) {
    window.minimumAvailableWidth = 700; // act as if medium
} else if (query.indexOf('-lg') != -1) {
    window.minimumAvailableWidth = 1000; // act as if large
} else {
    // Use the screen size to decide which UI layout is most suitable for this device
    window.minimumAvailableWidth = calculateMinimumAvailableWidth(query);
}

if (window.minimumAvailableWidth >= LARGE_UI_MINIMUM_WIDTH) {
    // Large layout
    window.useSmallUI = false;
    window.useSmallTiles = false;
    window.useSmallCards = true;
    window.useListView = false;
    uisize = "large";
} else if (window.minimumAvailableWidth >= MEDIUM_UI_MINIMUM_WIDTH) {
    // Small Tablet Layout
    window.useSmallUI = true;
    window.useSmallTiles = false;
    window.useSmallCards = true;
    window.useListView = false;
    uisize = "medium";
} else {
    // Phone layout
    window.useSmallUI = true;
    window.useSmallTiles = true;
    window.useSmallCards = false;
    window.useListView = true;
    uisize = "small";
}
   
// load correct CSS and add marker classes 
/* jshint -W060 */
document.write(document.getElementById(uisize + "-ui-styles").innerHTML);
document.documentElement.className += (window.useSmallUI ? "small" : "large") +
    (window.useSmallTiles ? " smallTiles" : " largeTiles");

if (window.useListView) {
    document.documentElement.className += " listView";
} else {
    document.documentElement.className += (window.useSmallCards ? " smallCards" : " largeCards");
}

if (query.indexOf('-nocustom') == -1) {
    /* jshint -W060 */
    document.write('<link href="custom/style.css" rel="stylesheet" id="customStyle" />');
}