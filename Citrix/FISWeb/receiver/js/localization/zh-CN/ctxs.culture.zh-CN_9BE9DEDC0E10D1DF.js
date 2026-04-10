(function ($) {
    var cultureSettings = {
        name: "zh-CN",
        englishName: "Chinese (Simplified, PRC)",
        nativeName: "简体中文(中华人民共和国)",
        stringBundle: "receiver/js/localization/zh-CN/ctxs.strings.zh-CN_89B2D610B89A6831.js",
        customStringBundle: "custom/strings.zh-CN.js"
    };
    
    $.globalization.availableCulture("zh-CN", cultureSettings);
    $.globalization.availableCulture("zh-Hans", cultureSettings);
})(jQuery);