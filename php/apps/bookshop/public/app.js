// ====================================================================
// Shape API helper — all calls go through POST /api.php
// ====================================================================
const API_URL = 'api.php';

async function api(op, payload) {
  const body = payload !== undefined ? { op, payload } : { op };
  const headers = { 'Content-Type': 'application/json' };

  const token = sessionStorage.getItem('jwt');
  if (token) headers['Authorization'] = 'Bearer ' + token;

  const res = await fetch(API_URL, {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
  });
  const data = await res.json();
  if (data.error) throw new Error(data.error.message || JSON.stringify(data.error));
  return data;
}

async function query(queries) {
  const data = await api('query', { queries });
  const out = {};
  for (const r of data.results) {
    const cols = r.meta.columns;
    out[r.ref] = r.rows.map(row => {
      const obj = {};
      cols.forEach((c, i) => obj[c] = row[i]);
      return obj;
    });
  }
  return out;
}

async function mutate(mutations, transaction = false) {
  return api('mutation', { mutations, transaction });
}

// ====================================================================
// Vue App
// ====================================================================
const { createApp, ref, computed, onMounted, watch } = Vue;

createApp({
  setup() {
    // ================================================================
    // State
    // ================================================================
    const view = ref('books');
    const loading = ref(false);
    const error = ref('');

    // Auth
    const user = ref(null);
    const loginForm = ref({ email: '', password: '' });
    const loginError = ref('');

    // Data
    const authors = ref([]);
    const books = ref([]);
    const reviews = ref([]);

    // Filters
    const search = ref('');
    const authorFilter = ref(0);
    const sortKey = ref('title');
    const sortAsc = ref(true);

    // Detail
    const selectedBook = ref(null);
    const bookReviews = ref([]);

    // Forms
    const showBookForm = ref(false);
    const bookForm = ref({ title: '', author_id: 0, price: '', isbn: '', stock: 0 });
    const bookFormError = ref('');
    const editingBook = ref(null);

    const showReviewForm = ref(false);
    const reviewForm = ref({ reviewer: '', rating: 5, comment: '' });
    const reviewFormError = ref('');

    // ================================================================
    // Auth
    // ================================================================
    async function login() {
      loginError.value = '';
      try {
        const data = await api('auth.token', {
          email: loginForm.value.email,
          password: loginForm.value.password,
        });
        sessionStorage.setItem('jwt', data.access_token);
        user.value = data.user;
        await loadAll();
      } catch (e) {
        loginError.value = e.message;
      }
    }

    function logout() {
      sessionStorage.removeItem('jwt');
      user.value = null;
      books.value = [];
      authors.value = [];
      reviews.value = [];
      selectedBook.value = null;
    }

    // ================================================================
    // Data Loading
    // ================================================================
    async function loadAll() {
      loading.value = true;
      error.value = '';
      try {
        const data = await query([
          { ref: 'authors', sql: 'authors.list', params: {} },
          { ref: 'books', sql: 'books.list', params: {} },
        ]);
        authors.value = data.authors;
        books.value = data.books;
      } catch (e) {
        error.value = e.message;
      } finally {
        loading.value = false;
      }
    }

    async function openBook(id) {
      loading.value = true;
      try {
        const data = await query([
          { ref: 'book', sql: 'books.get', params: { id } },
          { ref: 'reviews', sql: 'reviews.byBookId', params: { book_id: id } },
        ]);
        selectedBook.value = data.book[0] || null;
        bookReviews.value = data.reviews;
      } catch (e) {
        error.value = e.message;
      } finally {
        loading.value = false;
      }
    }

    function closeDetail() {
      selectedBook.value = null;
      bookReviews.value = [];
    }

    // ================================================================
    // Computed
    // ================================================================
    const filteredBooks = computed(() => {
      let list = [...books.value];

      if (authorFilter.value) {
        list = list.filter(b => b.author_id === authorFilter.value);
      }
      if (search.value) {
        const q = search.value.toLowerCase();
        list = list.filter(b =>
          (b.title || '').toLowerCase().includes(q) ||
          (b.isbn || '').toLowerCase().includes(q)
        );
      }

      list.sort((a, b) => {
        let av = a[sortKey.value], bv = b[sortKey.value];
        if (typeof av === 'string') av = av.toLowerCase();
        if (typeof bv === 'string') bv = bv.toLowerCase();
        if (av < bv) return sortAsc.value ? -1 : 1;
        if (av > bv) return sortAsc.value ? 1 : -1;
        return 0;
      });

      return list;
    });

    const totalBooks = computed(() => books.value.length);
    const totalAuthors = computed(() => authors.value.length);
    const totalStock = computed(() => books.value.reduce((sum, b) => sum + (b.stock || 0), 0));

    function authorName(id) {
      const a = authors.value.find(x => x.id === id);
      return a ? a.name : '—';
    }

    function authorBooks(authorId) {
      return books.value.filter(b => b.author_id === authorId);
    }

    // ================================================================
    // Sorting
    // ================================================================
    function sortBy(key) {
      if (sortKey.value === key) {
        sortAsc.value = !sortAsc.value;
      } else {
        sortKey.value = key;
        sortAsc.value = true;
      }
    }

    function sortIcon(key) {
      if (sortKey.value !== key) return 'bi-chevron-expand';
      return sortAsc.value ? 'bi-chevron-up' : 'bi-chevron-down';
    }

    // ================================================================
    // Book CRUD
    // ================================================================
    function openNewBook() {
      editingBook.value = null;
      bookForm.value = { title: '', author_id: authors.value[0]?.id || 0, price: '', isbn: '', stock: 0 };
      bookFormError.value = '';
      showBookForm.value = true;
    }

    function openEditBook(book) {
      editingBook.value = book;
      bookForm.value = {
        title: book.title,
        author_id: book.author_id,
        price: book.price,
        isbn: book.isbn || '',
        stock: book.stock || 0,
      };
      bookFormError.value = '';
      showBookForm.value = true;
    }

    async function saveBook() {
      bookFormError.value = '';
      const data = {
        title: bookForm.value.title,
        author_id: bookForm.value.author_id,
        price: parseFloat(bookForm.value.price),
        isbn: bookForm.value.isbn || null,
        stock: parseInt(bookForm.value.stock) || 0,
      };

      try {
        if (editingBook.value) {
          await mutate([{
            table: 'books', action: 'update',
            filter: { id: editingBook.value.id },
            data,
          }]);
        } else {
          await mutate([{
            table: 'books', action: 'insert', data,
          }]);
        }
        showBookForm.value = false;
        await loadAll();
        if (selectedBook.value) await openBook(selectedBook.value.id);
      } catch (e) {
        bookFormError.value = e.message;
      }
    }

    async function deleteBook(id) {
      try {
        await mutate([{ table: 'books', action: 'delete', filter: { id } }]);
        closeDetail();
        await loadAll();
      } catch (e) {
        error.value = e.message;
      }
    }

    // ================================================================
    // Review CRUD
    // ================================================================
    function openNewReview() {
      reviewForm.value = { reviewer: '', rating: 5, comment: '' };
      reviewFormError.value = '';
      showReviewForm.value = true;
    }

    async function saveReview() {
      reviewFormError.value = '';
      try {
        await mutate([{
          table: 'reviews', action: 'insert',
          data: {
            book_id: selectedBook.value.id,
            reviewer: reviewForm.value.reviewer,
            rating: parseInt(reviewForm.value.rating),
            comment: reviewForm.value.comment || null,
          },
        }]);
        showReviewForm.value = false;
        await openBook(selectedBook.value.id);
      } catch (e) {
        reviewFormError.value = e.message;
      }
    }

    async function deleteReview(id) {
      try {
        await mutate([{ table: 'reviews', action: 'delete', filter: { id } }]);
        await openBook(selectedBook.value.id);
      } catch (e) {
        error.value = e.message;
      }
    }

    // ================================================================
    // Lifecycle
    // ================================================================
    onMounted(() => {
      const token = sessionStorage.getItem('jwt');
      if (token) {
        user.value = { email: '...' };
        loadAll().catch(() => {
          sessionStorage.removeItem('jwt');
          user.value = null;
        });
      }
    });

    // ================================================================
    // Return public API
    // ================================================================
    return {
      view, loading, error,
      user, loginForm, loginError, login, logout,
      authors, books, reviews,
      search, authorFilter, sortKey, sortAsc,
      filteredBooks, totalBooks, totalAuthors, totalStock,
      authorName, authorBooks,
      sortBy, sortIcon,
      selectedBook, bookReviews, openBook, closeDetail,
      showBookForm, bookForm, bookFormError, editingBook,
      openNewBook, openEditBook, saveBook, deleteBook,
      showReviewForm, reviewForm, reviewFormError,
      openNewReview, saveReview, deleteReview,
    };
  }
}).mount('#app');
