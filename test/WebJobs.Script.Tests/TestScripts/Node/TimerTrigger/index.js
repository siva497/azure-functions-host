﻿var fs = require('fs');

module.exports = function (context) {
    var timeStamp = new Date().toISOString();    
    fs.appendFile('joblog.txt', timeStamp + '\r\n', function (err) {
        var blobOutputContents = "from timer trigger: " + timeStamp;
        context.done(err, blobOutputContents);
    });
}