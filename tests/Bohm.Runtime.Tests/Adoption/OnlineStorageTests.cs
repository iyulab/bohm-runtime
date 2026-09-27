using Bohm.Runtime.Adoption;

namespace Bohm.Runtime.Tests.Adoption;

public sealed class OnlineStorageTests
{
    private const string FirestoreModule = """
        <script type="module">
        import { initializeApp } from "https://www.gstatic.com/firebasejs/11.6.1/firebase-app.js";
        import { getFirestore, addDoc, collection } from "https://www.gstatic.com/firebasejs/11.6.1/firebase-firestore.js";
        const db = getFirestore(initializeApp(JSON.parse(typeof __firebase_config !== 'undefined' ? __firebase_config : '{}')));
        document.querySelector('form').onsubmit = () => addDoc(collection(db, 'entries'), { text: 'x' });
        </script>
        """;

    [Fact]
    public void An_application_that_keeps_its_data_only_in_Firestore_is_recognized() =>
        Assert.Equal("firestore", OnlineStorage.OnlyOnline(FirestoreModule));

    [Fact]
    public void Firestore_through_a_bundled_SDK_is_recognized_by_its_entry_point() =>
        Assert.Equal("firestore", OnlineStorage.OnlyOnline("<script>const db = firebase.getFirestore (app);</script>"));

    [Theory]
    [InlineData("localStorage.setItem('entries', json)")]
    [InlineData("localStorage['entries'] = json")]
    [InlineData("const req = indexedDB.open('app', 1)")]
    [InlineData("initializeFirestore(app, { localCache: persistentLocalCache() })")]
    [InlineData("enableIndexedDbPersistence(db)")]
    public void An_application_that_also_writes_locally_is_not_reported(string local) =>
        Assert.Null(OnlineStorage.OnlyOnline(FirestoreModule + "<script>" + local + "</script>"));

    [Theory]
    [InlineData("<p>no storage at all</p>")]
    [InlineData("<script>localStorage.setItem('a', '1')</script>")]
    [InlineData("<script>const firestoreNote = 'mentions firestore in a string only';</script>")]
    public void An_application_without_Firestore_is_not_reported(string html) =>
        Assert.Null(OnlineStorage.OnlyOnline(html));
}
