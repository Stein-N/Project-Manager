// Anchors scroll inside the page; every other link is handed to Java (opened in the browser / default app)
document.addEventListener('click', function (event) {
    var link = event.target.closest('a');
    if (!link) return;
    var href = link.getAttribute('href') || '';
    event.preventDefault();
    if (href.charAt(0) === '#') {
        var id = decodeURIComponent(href.substring(1));
        var target = document.getElementById(id) || document.getElementsByName(id)[0];
        if (target) target.scrollIntoView({behavior: 'smooth', block: 'start'});
        return;
    }
    // the Java side listens for alerts with this prefix (no JavaScript bridge object needed)
    alert('javaLink:' + link.href);
}, true);
