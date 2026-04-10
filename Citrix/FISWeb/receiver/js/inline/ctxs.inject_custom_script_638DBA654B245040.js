var query = window.location.search + window.location.hash;

if (query.indexOf('-nocustom') == -1) {
    /* jshint -W060 */
    document.write('<script src="custom/script.js"></scri' + 'pt>');
    /* jshint -W060 */
    document.write('<script src="custom/strings.en.js"></scri' + 'pt>');
}