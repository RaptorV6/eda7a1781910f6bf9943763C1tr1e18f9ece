(function ($) {
    var cultureSettings = {
        name: "zh-TW",
        englishName: "Chinese (Traditional, Taiwan)",
        nativeName: "繁體中文(台灣)",
        stringBundle: "receiver/js/localization/zh-TW/ctxs.strings.zh-TW_41B42EA2BF454B6D.js",
        customStringBundle: "custom/strings.zh-TW.js"
    };
    
    $.globalization.availableCulture("zh-TW", cultureSettings);
    $.globalization.availableCulture("zh-Hant", cultureSettings);
})(jQuery);