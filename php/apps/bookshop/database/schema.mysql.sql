-- Bookshop schema for MySQL / MariaDB (InnoDB, utf8mb4).
-- Usage: mysql -u USER -p DBNAME < schema.mysql.sql

CREATE TABLE IF NOT EXISTS authors (
  id INT AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(200) NOT NULL,
  email VARCHAR(200) NULL,
  bio TEXT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS books (
  id INT AUTO_INCREMENT PRIMARY KEY,
  title VARCHAR(300) NOT NULL,
  author_id INT NOT NULL,
  price DECIMAL(10,2) NOT NULL,
  isbn VARCHAR(20) NULL,
  stock INT NOT NULL DEFAULT 0,
  CONSTRAINT fk_books_author FOREIGN KEY (author_id) REFERENCES authors(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS reviews (
  id INT AUTO_INCREMENT PRIMARY KEY,
  book_id INT NOT NULL,
  reviewer VARCHAR(200) NOT NULL,
  rating INT NOT NULL,
  comment TEXT NULL,
  CONSTRAINT fk_reviews_book FOREIGN KEY (book_id) REFERENCES books(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS users (
  id INT AUTO_INCREMENT PRIMARY KEY,
  email VARCHAR(200) NOT NULL UNIQUE,
  password_hash VARCHAR(255) NOT NULL,
  display_name VARCHAR(200) NOT NULL,
  role VARCHAR(50) NOT NULL DEFAULT 'user',
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS refresh_tokens (
  id INT AUTO_INCREMENT PRIMARY KEY,
  user_id INT NOT NULL,
  token_hash VARCHAR(255) NOT NULL UNIQUE,
  expires_at DATETIME NOT NULL,
  revoked_at DATETIME NULL,
  replaced_by_token_id INT NULL,
  last_used_at DATETIME NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_refresh_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Sample data (create users separately, hashing passwords with PHP password_hash()).
INSERT INTO authors (id, name, email, bio) VALUES
  (1, 'Frank Herbert', 'frank@arrakis.com', 'Sci-fi'),
  (2, 'Isaac Asimov', 'isaac@foundation.org', 'Writer'),
  (3, 'Ursula K. Le Guin', NULL, 'Earthsea');

INSERT INTO books (id, title, author_id, price, isbn, stock) VALUES
  (1, 'Dune', 1, 12.99, '978-0441172719', 25),
  (2, 'Dune Messiah', 1, 10.99, '978-0593098233', 14),
  (3, 'Foundation', 2, 9.99, '978-0553293357', 30),
  (4, 'I, Robot', 2, 8.99, '978-0553382563', 18),
  (5, 'The Left Hand of Darkness', 3, 11.49, '978-0441478125', 20);

INSERT INTO reviews (id, book_id, reviewer, rating, comment) VALUES
  (1, 1, 'Paul Atreides', 5, 'The spice must flow'),
  (2, 1, 'Stilgar', 4, 'A worthy read'),
  (3, 3, 'Hari Seldon', 5, 'I predicted you would like this');
