// Initialize.
var pages = [];
var pageIndex = 0;
var end = 0;

var link = document.getElementById('_link');
var image = document.getElementById('_image');
var pi = document.getElementById('_pi');

var btnFirst = document.getElementById('_first');
var btnPrev = document.getElementById('_prev');
var btnNext = document.getElementById('_next');
var btnLast = document.getElementById('_last');

// Fix &amp; in thumbnail urls.
function htmlDecode(input) {
    var doc = new DOMParser().parseFromString(input, "text/html");
    return doc.documentElement.textContent;
}

// Call this after updating pageIndex to prevent the client from changing pages before the image has loaded.
function freezeButtons() {
    btnFirst.disabled = true;
    btnPrev.disabled = true;
    btnNext.disabled = true;
    btnLast.disabled = true;
}

// This sets the link and image according to the current pageIndex.
function populate() {
    link.href = htmlDecode(pages[pageIndex].link);
    image.src = htmlDecode(pages[pageIndex].image);
    pi.innerText = (pageIndex+1) + '/' + (end+1);
    freezeButtons();
}

// This is called by the comic image onload, this unlocks the buttons after the image has loaded and sets them to the appropriate state.
function updateButtonState() {
    if (pageIndex == 0) {
        btnNext.disabled = false;
        btnLast.disabled = false;
    }
    else if (pageIndex == end) {
        btnFirst.disabled = false;
        btnPrev.disabled = false;
    }
    else {
        btnFirst.disabled = false;
        btnPrev.disabled = false;
        btnNext.disabled = false;
        btnLast.disabled = false;
    }
}

// onClick first.
function first() {
    pageIndex = 0;
    populate();
    return false;
}

// onClick prev.
function prev() {
    if (pageIndex - 1 >= 0)
        pageIndex -= 1;
    populate();
    return false;
}

// onClick next.
function next() {
    if (pageIndex + 1 <= end)
        pageIndex += 1;
    populate();
    return false;
}

// onClick last.
function last() {
    pageIndex = end;
    populate();
    return false;
}
